using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using Grpc.Core;

namespace EventStore.Core.Services.Transport.Grpc;

internal partial class PersistentSubscriptions
{
	private static readonly Operation GetInfoOperation = new Operation(Plugins.Authorization.Operations.Subscriptions.Statistics);
	public override async Task<GetInfoResp> GetInfo(GetInfoReq request, ServerCallContext context)
	{
		var getPersistentSubscriptionInfoSource = new TaskCompletionSource<GetInfoResp>(TaskCreationOptions.RunContinuationsAsynchronously);

		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user,
			GetInfoOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		string streamId = request.Options.StreamOptionCase switch
		{
			GetInfoReq.Types.Options.StreamOptionOneofCase.All => "$all",
			GetInfoReq.Types.Options.StreamOptionOneofCase.StreamIdentifier => request.Options.StreamIdentifier,
			_ => throw new InvalidOperationException()
		};

		_publisher.Publish(new MonitoringMessage.GetPersistentSubscriptionStats(
			new CallbackEnvelope(HandleGetPersistentSubscriptionStatsCompleted),
			streamId,
			request.Options.GroupName));
		return await getPersistentSubscriptionInfoSource.Task;

		void HandleGetPersistentSubscriptionStatsCompleted(Message message)
		{
			if (message is ClientMessage.NotHandled notHandled && RpcExceptions.TryHandleNotHandled(notHandled, out var ex))
			{
				getPersistentSubscriptionInfoSource.TrySetException(ex);
				return;
			}

			if (message is MonitoringMessage.GetPersistentSubscriptionStatsCompleted completed)
			{
				switch (completed.Result)
				{
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success:
						var getInfoResp = new GetInfoResp
						{
							SubscriptionInfo = ParseSubscriptionInfo(completed.SubscriptionStats.First())
						};
						getPersistentSubscriptionInfoSource.TrySetResult(getInfoResp);
						return;
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotFound:
						getPersistentSubscriptionInfoSource.TrySetException(
							RpcExceptions.PersistentSubscriptionDoesNotExist(streamId,
								request.Options.GroupName));
						return;
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady:
						getPersistentSubscriptionInfoSource.TrySetException(
							RpcExceptions.ServerNotReady());
						return;
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Fail:
						getPersistentSubscriptionInfoSource.TrySetException(
							RpcExceptions.PersistentSubscriptionFailed(streamId, request.Options.GroupName,
								completed.ErrorString));
						return;
					default:
						getPersistentSubscriptionInfoSource.TrySetException(
							RpcExceptions.UnknownError(completed.Result));
						return;
				}
			}
			getPersistentSubscriptionInfoSource.TrySetException(
				RpcExceptions.UnknownMessage<MonitoringMessage.GetPersistentSubscriptionStatsCompleted>(
					message));
		}
	}

	public override async Task<ListResp> List(ListReq request, ServerCallContext context)
	{
		var listPersistentSubscriptionsSource =
			new TaskCompletionSource<ListResp>(TaskCreationOptions.RunContinuationsAsynchronously);

		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user,
			GetInfoOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var options = request.Options ?? new ListReq.Types.Options();
		var listOptionCase = options.ListOptionCase == ListReq.Types.Options.ListOptionOneofCase.None
			? ListReq.Types.Options.ListOptionOneofCase.ListAllSubscriptions
			: options.ListOptionCase;

		var hasPaging = options.HasOffset || options.HasCount;
		if (options.HasOffset && options.Offset < 0)
			throw new RpcException(new Status(StatusCode.InvalidArgument, "offset must be a non-negative integer"));
		if (options.HasCount && options.Count < 1)
			throw new RpcException(new Status(StatusCode.InvalidArgument, "count must be a positive integer"));
		if (hasPaging && !(options.HasOffset && options.HasCount))
			throw new RpcException(new Status(StatusCode.InvalidArgument, "offset and count must be provided together"));
		if (hasPaging &&
			listOptionCase != ListReq.Types.Options.ListOptionOneofCase.ListAllSubscriptions)
			throw new RpcException(new Status(StatusCode.InvalidArgument,
				"offset and count are only supported when listing all subscriptions"));

		var streamId = string.Empty;
		switch (listOptionCase)
		{
			case ListReq.Types.Options.ListOptionOneofCase.ListAllSubscriptions:
				var envelope = new CallbackEnvelope(HandleListSubscriptionsCompleted);
				_publisher.Publish(hasPaging
					? new MonitoringMessage.GetAllPersistentSubscriptionStats(
						envelope,
						options.Offset,
						options.Count)
					: new MonitoringMessage.GetAllPersistentSubscriptionStats(envelope));
				break;
			case ListReq.Types.Options.ListOptionOneofCase.ListForStream:
				var listForStream = options.ListForStream;
				if (listForStream is null)
					throw new RpcException(new Status(StatusCode.InvalidArgument, "list_for_stream must be provided"));
				streamId = listForStream.StreamOptionCase switch
				{
					ListReq.Types.StreamOption.StreamOptionOneofCase.All => "$all",
					ListReq.Types.StreamOption.StreamOptionOneofCase.Stream => listForStream.Stream,
					_ => throw new RpcException(new Status(StatusCode.InvalidArgument, "stream option must be provided"))
				};
				_publisher.Publish(new MonitoringMessage.GetStreamPersistentSubscriptionStats(
					new CallbackEnvelope(HandleListSubscriptionsCompleted),
					streamId
				));
				break;
			default:
				throw new InvalidOperationException();
		}

		return await listPersistentSubscriptionsSource.Task.WaitAsync(context.CancellationToken);

		void HandleListSubscriptionsCompleted(Message message)
		{
			if (message is ClientMessage.NotHandled notHandled && RpcExceptions.TryHandleNotHandled(notHandled, out var ex))
			{
				listPersistentSubscriptionsSource.TrySetException(ex);
				return;
			}

			if (message is MonitoringMessage.GetPersistentSubscriptionStatsCompleted completed)
			{
				switch (completed.Result)
				{
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success:
						var listResp = new ListResp();
						listResp.Subscriptions.AddRange(
							completed.SubscriptionStats.Select(ParseSubscriptionInfo)
						);
						listResp.Offset = completed.RequestedOffset;
						listResp.Count = completed.RequestedCount == int.MaxValue
							? listResp.Subscriptions.Count
							: completed.RequestedCount;
						listResp.Total = completed.Total;
						listPersistentSubscriptionsSource.TrySetResult(listResp);
						return;
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotFound:
						listPersistentSubscriptionsSource.TrySetException(
							RpcExceptions.PersistentSubscriptionDoesNotExist(streamId, ""));
						return;
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady:
						listPersistentSubscriptionsSource.TrySetException(
							RpcExceptions.ServerNotReady());
						return;
					case MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Fail:
						listPersistentSubscriptionsSource.TrySetException(
							RpcExceptions.PersistentSubscriptionFailed(streamId, "", completed.ErrorString));
						return;
					default:
						listPersistentSubscriptionsSource.TrySetException(
							RpcExceptions.UnknownError(completed.Result));
						return;
				}
			}
			listPersistentSubscriptionsSource.TrySetException(
				RpcExceptions.UnknownMessage<MonitoringMessage.GetPersistentSubscriptionStatsCompleted>(
					message));
		}
	}

	private SubscriptionInfo ParseSubscriptionInfo(MonitoringMessage.PersistentSubscriptionInfo input)
	{
		var connectionInfo = new List<SubscriptionInfo.Types.ConnectionInfo>();
		foreach (var conn in input.Connections)
		{
			var connInfo = new SubscriptionInfo.Types.ConnectionInfo
			{
				From = conn.From,
				Username = conn.Username,
				AverageItemsPerSecond = conn.AverageItemsPerSecond,
				TotalItems = conn.TotalItems,
				CountSinceLastMeasurement = conn.CountSinceLastMeasurement,
				AvailableSlots = conn.AvailableSlots,
				InFlightMessages = conn.InFlightMessages,
				ConnectionName = conn.ConnectionName
			};
			connInfo.ObservedMeasurements.AddRange(
				conn.ObservedMeasurements.Select(x => new SubscriptionInfo.Types.Measurement
				{ Key = x.Key, Value = x.Value }));
			connectionInfo.Add(connInfo);
		}

		var subscriptionInfo = new SubscriptionInfo
		{
			EventSource = input.EventSource,
			GroupName = input.GroupName,
			Status = input.Status,
			AveragePerSecond = input.AveragePerSecond,
			TotalItems = input.TotalItems,
			CountSinceLastMeasurement = input.CountSinceLastMeasurement,
			LastCheckpointedEventPosition = input.LastCheckpointedEventPosition ?? string.Empty,
			LastKnownEventPosition = input.LastKnownEventPosition ?? string.Empty,
			ResolveLinkTos = input.ResolveLinktos,
			StartFrom = input.StartFrom,
			MessageTimeoutMilliseconds = input.MessageTimeoutMilliseconds,
			ExtraStatistics = input.ExtraStatistics,
			MaxRetryCount = input.MaxRetryCount,
			LiveBufferSize = input.LiveBufferSize,
			BufferSize = input.BufferSize,
			ReadBatchSize = input.ReadBatchSize,
			CheckPointAfterMilliseconds = input.CheckPointAfterMilliseconds,
			MinCheckPointCount = input.MinCheckPointCount,
			MaxCheckPointCount = input.MaxCheckPointCount,
			ReadBufferCount = input.ReadBufferCount,
			LiveBufferCount = input.LiveBufferCount,
			RetryBufferCount = input.RetryBufferCount,
			TotalInFlightMessages = input.TotalInFlightMessages,
			OutstandingMessagesCount = input.OutstandingMessagesCount,
			NamedConsumerStrategy = input.NamedConsumerStrategy,
			MaxSubscriberCount = input.MaxSubscriberCount,
			ParkedMessageCount = input.ParkedMessageCount,
		};
		subscriptionInfo.Connections.AddRange(connectionInfo);
		return subscriptionInfo;
	}
}
