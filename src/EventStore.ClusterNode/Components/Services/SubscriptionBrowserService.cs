using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class SubscriptionBrowserService(
	IPublisher publisher,
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation StatisticsOperation = new(Operations.Subscriptions.Statistics);
	private static readonly Operation CreateOperation = new(Operations.Subscriptions.Create);
	private static readonly Operation UpdateOperation = new(Operations.Subscriptions.Update);
	private static readonly Operation DeleteOperation = new(Operations.Subscriptions.Delete);
	private static readonly Operation ReplayParkedOperation = new(Operations.Subscriptions.ReplayParked);
	public static readonly IReadOnlyList<SubscriptionStrategyOption> StrategyOptions = [
		new(SystemConsumerStrategies.RoundRobin, "Round robin"),
		new(SystemConsumerStrategies.DispatchToSingle, "Dispatch to single"),
		new(SystemConsumerStrategies.Pinned, "Pinned"),
		new(SystemConsumerStrategies.PinnedByCorrelation, "Pinned by correlation")
	];

	public async Task<SubscriptionListPage> ReadAll(CancellationToken cancellationToken = default) {
		if (!await HasAccess(StatisticsOperation, cancellationToken))
			return SubscriptionListPage.Unavailable("Persistent subscription statistics access was denied.");

		var envelope = new TaskCompletionEnvelope<MonitoringMessage.GetPersistentSubscriptionStatsCompleted>();
		publisher.Publish(new MonitoringMessage.GetAllPersistentSubscriptionStats(envelope));

		MonitoringMessage.GetPersistentSubscriptionStatsCompleted completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionListPage.Unavailable("Timed out reading persistent subscriptions.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionListPage.Unavailable($"Unable to read persistent subscriptions: {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success =>
				SubscriptionListPage.Success(completed.SubscriptionStats
					?.Select(SubscriptionView.From)
					.OrderBy(x => x.StreamId, StringComparer.OrdinalIgnoreCase)
					.ThenBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
					.ToArray() ?? Array.Empty<SubscriptionView>()),
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotFound =>
				SubscriptionListPage.Success(Array.Empty<SubscriptionView>()),
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady =>
				SubscriptionListPage.Unavailable("Persistent subscriptions are not ready."),
			_ => SubscriptionListPage.Unavailable(string.IsNullOrWhiteSpace(completed.ErrorString)
				? $"Unable to read persistent subscriptions. Result: {completed.Result}."
				: completed.ErrorString)
		};
	}

	public async Task<SubscriptionDetailPage> Read(string streamId, string groupName, CancellationToken cancellationToken = default) {
		var validation = ValidateIdentity(streamId, groupName);
		if (!validation.Success)
			return SubscriptionDetailPage.Unavailable(streamId, groupName, validation.Message);

		streamId = streamId.Trim();
		groupName = groupName.Trim();
		var operation = WithSubscriptionParameters(StatisticsOperation, streamId, groupName);
		if (!await HasAccess(operation, cancellationToken))
			return SubscriptionDetailPage.Unavailable(streamId, groupName, $"Subscription '{groupName}' access was denied.");

		var envelope = new TaskCompletionEnvelope<MonitoringMessage.GetPersistentSubscriptionStatsCompleted>();
		publisher.Publish(new MonitoringMessage.GetPersistentSubscriptionStats(envelope, streamId, groupName));

		MonitoringMessage.GetPersistentSubscriptionStatsCompleted completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionDetailPage.Unavailable(streamId, groupName, $"Timed out reading subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionDetailPage.Unavailable(streamId, groupName, $"Unable to read subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success =>
				completed.SubscriptionStats?.Select(SubscriptionView.From).FirstOrDefault() is { } subscription
					? SubscriptionDetailPage.Success(subscription)
					: SubscriptionDetailPage.Unavailable(streamId, groupName, $"Subscription '{groupName}' on stream '{streamId}' was not found."),
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotFound =>
				SubscriptionDetailPage.Unavailable(streamId, groupName, $"Subscription '{groupName}' on stream '{streamId}' was not found."),
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady =>
				SubscriptionDetailPage.Unavailable(streamId, groupName, "Persistent subscriptions are not ready."),
			_ => SubscriptionDetailPage.Unavailable(streamId, groupName, string.IsNullOrWhiteSpace(completed.ErrorString)
				? $"Unable to read subscription '{groupName}'. Result: {completed.Result}."
				: completed.ErrorString)
		};
	}

	public async Task<SubscriptionCommandResult> Create(SubscriptionUpdateRequest request, CancellationToken cancellationToken = default) {
		var validation = ValidateRequest(request);
		if (!validation.Success)
			return validation;

		var streamId = request.StreamId.Trim();
		var groupName = request.GroupName.Trim();
		var operation = WithSubscriptionParameters(CreateOperation, streamId, groupName);
		if (!await HasAccess(operation, cancellationToken))
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' creation access was denied.");

		if (IsAllStream(streamId))
			return await CreateAll(request, groupName, cancellationToken);

		if (!request.StartFrom.TryGetStreamEventNumber(out var startFrom))
			return SubscriptionCommandResult.Failure(streamId, groupName, "Start from must be an event number.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.CreatePersistentSubscriptionToStreamCompleted>();
		ClientMessage.CreatePersistentSubscriptionToStreamCompleted completed;
		try {
			publisher.Publish(new ClientMessage.CreatePersistentSubscriptionToStream(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				streamId,
				groupName,
				request.ResolveLinkTos,
				startFrom,
				request.MessageTimeoutMilliseconds,
				request.ExtraStatistics,
				request.MaxRetryCount,
				request.BufferSize,
				request.LiveBufferSize,
				request.ReadBatchSize,
				request.CheckPointAfterMilliseconds,
				request.MinCheckPointCount,
				request.MaxCheckPointCount,
				request.MaxSubscriberCount,
				request.NamedConsumerStrategy,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Timed out creating subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Unable to create subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.CreatePersistentSubscriptionToStreamCompleted.CreatePersistentSubscriptionToStreamResult.Success =>
				SubscriptionCommandResult.Succeeded(streamId, groupName, $"Subscription '{groupName}' was created."),
			ClientMessage.CreatePersistentSubscriptionToStreamCompleted.CreatePersistentSubscriptionToStreamResult.AlreadyExists =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' already exists."),
			ClientMessage.CreatePersistentSubscriptionToStreamCompleted.CreatePersistentSubscriptionToStreamResult.AccessDenied =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' creation access was denied."),
			_ => SubscriptionCommandResult.Failure(streamId, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	public async Task<SubscriptionCommandResult> Update(SubscriptionUpdateRequest request, CancellationToken cancellationToken = default) {
		var validation = ValidateRequest(request);
		if (!validation.Success)
			return validation;

		var streamId = request.StreamId.Trim();
		var groupName = request.GroupName.Trim();
		var operation = WithSubscriptionParameters(UpdateOperation, streamId, groupName);
		if (!await HasAccess(operation, cancellationToken))
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' update access was denied.");

		if (IsAllStream(streamId))
			return await UpdateAll(request, groupName, cancellationToken);

		if (!request.StartFrom.TryGetStreamEventNumber(out var startFrom))
			return SubscriptionCommandResult.Failure(streamId, groupName, "Start from must be an event number.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.UpdatePersistentSubscriptionToStreamCompleted>();
		ClientMessage.UpdatePersistentSubscriptionToStreamCompleted completed;
		try {
			publisher.Publish(new ClientMessage.UpdatePersistentSubscriptionToStream(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				streamId,
				groupName,
				request.ResolveLinkTos,
				startFrom,
				request.MessageTimeoutMilliseconds,
				request.ExtraStatistics,
				request.MaxRetryCount,
				request.BufferSize,
				request.LiveBufferSize,
				request.ReadBatchSize,
				request.CheckPointAfterMilliseconds,
				request.MinCheckPointCount,
				request.MaxCheckPointCount,
				request.MaxSubscriberCount,
				request.NamedConsumerStrategy,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Timed out updating subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Unable to update subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult.Success =>
				SubscriptionCommandResult.Succeeded(streamId, groupName, $"Subscription '{groupName}' was updated."),
			ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult.DoesNotExist =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' was not found."),
			ClientMessage.UpdatePersistentSubscriptionToStreamCompleted.UpdatePersistentSubscriptionToStreamResult.AccessDenied =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' update access was denied."),
			_ => SubscriptionCommandResult.Failure(streamId, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	private async Task<SubscriptionCommandResult> CreateAll(SubscriptionUpdateRequest request, string groupName, CancellationToken cancellationToken) {
		if (!request.StartFrom.TryGetAllStreamPosition(out var startFrom))
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, "Start from must be a $all position like C:0/P:0.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.CreatePersistentSubscriptionToAllCompleted>();
		ClientMessage.CreatePersistentSubscriptionToAllCompleted completed;
		try {
			publisher.Publish(new ClientMessage.CreatePersistentSubscriptionToAll(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				groupName,
				eventFilter: null,
				request.ResolveLinkTos,
				startFrom,
				request.MessageTimeoutMilliseconds,
				request.ExtraStatistics,
				request.MaxRetryCount,
				request.BufferSize,
				request.LiveBufferSize,
				request.ReadBatchSize,
				request.CheckPointAfterMilliseconds,
				request.MinCheckPointCount,
				request.MaxCheckPointCount,
				request.MaxSubscriberCount,
				request.NamedConsumerStrategy,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Timed out creating subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Unable to create subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.CreatePersistentSubscriptionToAllCompleted.CreatePersistentSubscriptionToAllResult.Success =>
				SubscriptionCommandResult.Succeeded(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' was created."),
			ClientMessage.CreatePersistentSubscriptionToAllCompleted.CreatePersistentSubscriptionToAllResult.AlreadyExists =>
				SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' already exists."),
			ClientMessage.CreatePersistentSubscriptionToAllCompleted.CreatePersistentSubscriptionToAllResult.AccessDenied =>
				SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' creation access was denied."),
			_ => SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	private async Task<SubscriptionCommandResult> UpdateAll(SubscriptionUpdateRequest request, string groupName, CancellationToken cancellationToken) {
		if (!request.StartFrom.TryGetAllStreamPosition(out var startFrom))
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, "Start from must be a $all position like C:0/P:0.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.UpdatePersistentSubscriptionToAllCompleted>();
		ClientMessage.UpdatePersistentSubscriptionToAllCompleted completed;
		try {
			publisher.Publish(new ClientMessage.UpdatePersistentSubscriptionToAll(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				groupName,
				request.ResolveLinkTos,
				startFrom,
				request.MessageTimeoutMilliseconds,
				request.ExtraStatistics,
				request.MaxRetryCount,
				request.BufferSize,
				request.LiveBufferSize,
				request.ReadBatchSize,
				request.CheckPointAfterMilliseconds,
				request.MinCheckPointCount,
				request.MaxCheckPointCount,
				request.MaxSubscriberCount,
				request.NamedConsumerStrategy,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Timed out updating subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Unable to update subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.UpdatePersistentSubscriptionToAllCompleted.UpdatePersistentSubscriptionToAllResult.Success =>
				SubscriptionCommandResult.Succeeded(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' was updated."),
			ClientMessage.UpdatePersistentSubscriptionToAllCompleted.UpdatePersistentSubscriptionToAllResult.DoesNotExist =>
				SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' was not found."),
			ClientMessage.UpdatePersistentSubscriptionToAllCompleted.UpdatePersistentSubscriptionToAllResult.AccessDenied =>
				SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' update access was denied."),
			_ => SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	public async Task<SubscriptionCommandResult> Delete(string streamId, string groupName, CancellationToken cancellationToken = default) {
		var validation = ValidateIdentity(streamId, groupName);
		if (!validation.Success)
			return validation;

		streamId = streamId.Trim();
		groupName = groupName.Trim();
		var operation = WithSubscriptionParameters(DeleteOperation, streamId, groupName);
		if (!await HasAccess(operation, cancellationToken))
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' delete access was denied.");

		if (IsAllStream(streamId))
			return await DeleteAll(groupName, cancellationToken);

		var envelope = new TaskCompletionEnvelope<ClientMessage.DeletePersistentSubscriptionToStreamCompleted>();
		ClientMessage.DeletePersistentSubscriptionToStreamCompleted completed;
		try {
			publisher.Publish(new ClientMessage.DeletePersistentSubscriptionToStream(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				streamId,
				groupName,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Timed out deleting subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Unable to delete subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.DeletePersistentSubscriptionToStreamCompleted.DeletePersistentSubscriptionToStreamResult.Success =>
				SubscriptionCommandResult.Succeeded(streamId, groupName, $"Subscription '{groupName}' was deleted."),
			ClientMessage.DeletePersistentSubscriptionToStreamCompleted.DeletePersistentSubscriptionToStreamResult.DoesNotExist =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' was not found."),
			ClientMessage.DeletePersistentSubscriptionToStreamCompleted.DeletePersistentSubscriptionToStreamResult.AccessDenied =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' delete access was denied."),
			_ => SubscriptionCommandResult.Failure(streamId, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	private async Task<SubscriptionCommandResult> DeleteAll(string groupName, CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<ClientMessage.DeletePersistentSubscriptionToAllCompleted>();
		ClientMessage.DeletePersistentSubscriptionToAllCompleted completed;
		try {
			publisher.Publish(new ClientMessage.DeletePersistentSubscriptionToAll(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				groupName,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Timed out deleting subscription '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Unable to delete subscription '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.DeletePersistentSubscriptionToAllCompleted.DeletePersistentSubscriptionToAllResult.Success =>
				SubscriptionCommandResult.Succeeded(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' was deleted."),
			ClientMessage.DeletePersistentSubscriptionToAllCompleted.DeletePersistentSubscriptionToAllResult.DoesNotExist =>
				SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' was not found."),
			ClientMessage.DeletePersistentSubscriptionToAllCompleted.DeletePersistentSubscriptionToAllResult.AccessDenied =>
				SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, $"Subscription '{groupName}' delete access was denied."),
			_ => SubscriptionCommandResult.Failure(SystemStreams.AllStream, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	public async Task<SubscriptionCommandResult> ReplayParked(string streamId, string groupName, long? stopAt = null, CancellationToken cancellationToken = default) {
		var validation = ValidateIdentity(streamId, groupName);
		if (!validation.Success)
			return validation;

		if (stopAt.HasValue && stopAt.Value < 0)
			return SubscriptionCommandResult.Failure(streamId, groupName, "Stop at must be zero or greater.");

		streamId = streamId.Trim();
		groupName = groupName.Trim();
		var operation = WithSubscriptionParameters(ReplayParkedOperation, streamId, groupName);
		if (!await HasAccess(operation, cancellationToken))
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' replay access was denied.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.ReplayMessagesReceived>();
		ClientMessage.ReplayMessagesReceived completed;
		try {
			publisher.Publish(new ClientMessage.ReplayParkedMessages(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				streamId,
				groupName,
				stopAt,
				CurrentUser));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Timed out replaying parked messages for '{groupName}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return SubscriptionCommandResult.Failure(streamId, groupName, $"Unable to replay parked messages for '{groupName}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ClientMessage.ReplayMessagesReceived.ReplayMessagesReceivedResult.Success =>
				SubscriptionCommandResult.Succeeded(streamId, groupName, $"Parked messages for '{groupName}' are replaying."),
			ClientMessage.ReplayMessagesReceived.ReplayMessagesReceivedResult.DoesNotExist =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' was not found."),
			ClientMessage.ReplayMessagesReceived.ReplayMessagesReceivedResult.AccessDenied =>
				SubscriptionCommandResult.Failure(streamId, groupName, $"Subscription '{groupName}' replay access was denied."),
			_ => SubscriptionCommandResult.Failure(streamId, groupName, FriendlyResult(completed.Result.ToString(), completed.Reason))
		};
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static Operation WithSubscriptionParameters(Operation operation, string streamId, string groupName) =>
		operation
			.WithParameter(Operations.Subscriptions.Parameters.StreamId(streamId))
			.WithParameter(Operations.Subscriptions.Parameters.SubscriptionId(groupName));

	private static SubscriptionCommandResult ValidateIdentity(string streamId, string groupName) {
		if (string.IsNullOrWhiteSpace(streamId))
			return SubscriptionCommandResult.Failure("", groupName ?? "", "Enter a stream id.");

		if (string.IsNullOrWhiteSpace(groupName))
			return SubscriptionCommandResult.Failure(streamId, "", "Enter a subscription group name.");

		return SubscriptionCommandResult.Succeeded(streamId.Trim(), groupName.Trim(), "");
	}

	private static SubscriptionCommandResult ValidateRequest(SubscriptionUpdateRequest request) {
		if (request == null)
			return SubscriptionCommandResult.Failure("", "", "Subscription request is required.");

		var validation = ValidateIdentity(request.StreamId, request.GroupName);
		if (!validation.Success)
			return validation;

		if (request.BufferSize <= 0)
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Buffer size must be positive.");

		if (request.LiveBufferSize <= 0)
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Live buffer size must be positive.");

		if (request.ReadBatchSize <= 0)
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Read batch size must be positive.");

		if (request.BufferSize <= request.ReadBatchSize)
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Buffer size must be larger than read batch size.");

		if (request.MessageTimeoutMilliseconds <= 0 ||
		    request.MaxRetryCount < 0 ||
		    request.CheckPointAfterMilliseconds <= 0 ||
		    request.MinCheckPointCount < 0 ||
		    request.MaxCheckPointCount < request.MinCheckPointCount ||
		    request.MaxSubscriberCount <= 0)
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Review the numeric settings before saving.");

		if (!StrategyOptions.Any(x => string.Equals(x.Value, request.NamedConsumerStrategy, StringComparison.Ordinal)))
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Choose a valid consumer strategy.");

		if (IsAllStream(request.StreamId)) {
			if (!request.StartFrom.TryGetAllStreamPosition(out _))
				return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Start from must be a $all position like C:0/P:0.");
		} else if (!request.StartFrom.TryGetStreamEventNumber(out _)) {
			return SubscriptionCommandResult.Failure(request.StreamId, request.GroupName, "Start from must be an event number.");
		}

		return SubscriptionCommandResult.Succeeded(request.StreamId.Trim(), request.GroupName.Trim(), "");
	}

	private static bool IsAllStream(string streamId) =>
		string.Equals(streamId?.Trim(), SystemStreams.AllStream, StringComparison.Ordinal);

	private static string FriendlyResult(string result, string reason) =>
		string.IsNullOrWhiteSpace(reason) ? $"Persistent subscription command returned {result}." : reason;

}

public sealed record SubscriptionListPage(
	IReadOnlyList<SubscriptionView> Subscriptions,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasSubscriptions => Subscriptions.Count > 0;
	public int ConnectedCount => Subscriptions.Count(x => x.ConnectionCount > 0);
	public long ParkedMessageCount => Subscriptions.Sum(x => x.ParkedMessageCount);
	public int InFlightMessageCount => Subscriptions.Sum(x => x.TotalInFlightMessages);
	public string GroupCountLabel => IsAvailable ? Subscriptions.Count.ToString() : "-";
	public string ConnectedCountLabel => IsAvailable ? ConnectedCount.ToString() : "-";
	public string ParkedMessageCountLabel => IsAvailable ? ParkedMessageCount.ToString() : "-";
	public string InFlightMessageCountLabel => IsAvailable ? InFlightMessageCount.ToString() : "-";

	public static SubscriptionListPage Success(IReadOnlyList<SubscriptionView> subscriptions) => new(subscriptions, "");
	public static SubscriptionListPage Unavailable(string message) => new(Array.Empty<SubscriptionView>(), message);
}

public sealed record SubscriptionDetailPage(
	SubscriptionView Subscription,
	string StreamId,
	string GroupName,
	string Message) {
	public bool HasSubscription => Subscription is not null;

	public static SubscriptionDetailPage Success(SubscriptionView subscription) => new(subscription, subscription.StreamId, subscription.GroupName, "");
	public static SubscriptionDetailPage Unavailable(string streamId, string groupName, string message) => new(null, streamId, groupName, message);
}

public sealed record SubscriptionCommandResult(
	bool Success,
	string StreamId,
	string GroupName,
	string Message) {
	public static SubscriptionCommandResult Succeeded(string streamId, string groupName, string message) => new(true, streamId, groupName, message);
	public static SubscriptionCommandResult Failure(string streamId, string groupName, string message) => new(false, streamId, groupName, message);
}

public sealed record SubscriptionUpdateRequest(
	string StreamId,
	string GroupName,
	bool ResolveLinkTos,
	SubscriptionStartPosition StartFrom,
	int MessageTimeoutMilliseconds,
	bool ExtraStatistics,
	int MaxRetryCount,
	int LiveBufferSize,
	int BufferSize,
	int ReadBatchSize,
	int CheckPointAfterMilliseconds,
	int MinCheckPointCount,
	int MaxCheckPointCount,
	int MaxSubscriberCount,
	string NamedConsumerStrategy);

public sealed record SubscriptionStrategyOption(
	string Value,
	string Label);

public sealed class SubscriptionSettingsInput {
	[Required(ErrorMessage = "Enter a stream id.")]
	public string StreamId { get; set; } = "";

	[Required(ErrorMessage = "Enter a group name.")]
	public string GroupName { get; set; } = "";

	public bool ResolveLinkTos { get; set; }
	public string StartFrom { get; set; } = "0";
	public int MessageTimeoutMilliseconds { get; set; } = 10000;
	public bool ExtraStatistics { get; set; }
	public int MaxRetryCount { get; set; } = 10;
	public int LiveBufferSize { get; set; } = 500;
	public int BufferSize { get; set; } = 500;
	public int ReadBatchSize { get; set; } = 20;
	public int CheckPointAfterMilliseconds { get; set; } = 1000;
	public int MinCheckPointCount { get; set; } = 10;
	public int MaxCheckPointCount { get; set; } = 500;
	public int MaxSubscriberCount { get; set; } = 10;
	public string NamedConsumerStrategy { get; set; } = SystemConsumerStrategies.RoundRobin;

	public SubscriptionUpdateRequest ToRequest() =>
		new(
			StreamId,
			GroupName,
			ResolveLinkTos,
			SubscriptionStartPosition.From(StartFrom),
			MessageTimeoutMilliseconds,
			ExtraStatistics,
			MaxRetryCount,
			LiveBufferSize,
			BufferSize,
			ReadBatchSize,
			CheckPointAfterMilliseconds,
			MinCheckPointCount,
			MaxCheckPointCount,
			MaxSubscriberCount,
			NamedConsumerStrategy);

	public static SubscriptionSettingsInput From(SubscriptionUpdateRequest request) =>
		new() {
			StreamId = request.StreamId,
			GroupName = request.GroupName,
			ResolveLinkTos = request.ResolveLinkTos,
			StartFrom = request.StartFrom.Value,
			MessageTimeoutMilliseconds = request.MessageTimeoutMilliseconds,
			ExtraStatistics = request.ExtraStatistics,
			MaxRetryCount = request.MaxRetryCount,
			LiveBufferSize = request.LiveBufferSize,
			BufferSize = request.BufferSize,
			ReadBatchSize = request.ReadBatchSize,
			CheckPointAfterMilliseconds = request.CheckPointAfterMilliseconds,
			MinCheckPointCount = request.MinCheckPointCount,
			MaxCheckPointCount = request.MaxCheckPointCount,
			MaxSubscriberCount = request.MaxSubscriberCount,
			NamedConsumerStrategy = request.NamedConsumerStrategy
		};
}

public sealed record SubscriptionView(
	string StreamId,
	string GroupName,
	string Status,
	string LastCheckpointedEventPosition,
	string LastKnownEventPosition,
	string StartFrom,
	bool ResolveLinkTos,
	int MessageTimeoutMilliseconds,
	bool ExtraStatistics,
	int MaxRetryCount,
	int LiveBufferSize,
	int BufferSize,
	int ReadBatchSize,
	int CheckPointAfterMilliseconds,
	int MinCheckPointCount,
	int MaxCheckPointCount,
	string NamedConsumerStrategy,
	int AveragePerSecond,
	long TotalItems,
	int ConnectionCount,
	long ParkedMessageCount,
	int TotalInFlightMessages,
	int OutstandingMessagesCount,
	int ReadBufferCount,
	long LiveBufferCount,
	int RetryBufferCount,
	int MaxSubscriberCount,
	IReadOnlyList<SubscriptionConnectionView> Connections) {
	public string StatusLabel => string.IsNullOrWhiteSpace(Status) ? "Unknown" : Status;
	public string StatusTone => StatusLabel.Contains("live", StringComparison.OrdinalIgnoreCase)
		? "good"
		: StatusLabel.Contains("parked", StringComparison.OrdinalIgnoreCase) ||
		  StatusLabel.Contains("behind", StringComparison.OrdinalIgnoreCase)
			? "warn"
			: ConnectionCount == 0
				? "muted"
				: "";
	public string ConnectionLabel => ConnectionCount == 1 ? "1 consumer" : $"{ConnectionCount} consumers";
	public long? BehindByMessages => CalculateBehindByMessages(StreamId, LastKnownEventPosition, LastCheckpointedEventPosition);
	public string BehindStatusLabel {
		get {
			var behind = BehindByMessages;
			if (!behind.HasValue)
				return "unknown";

			if (behind.Value == 0)
				return "";

			if (AveragePerSecond <= 0)
				return $"{behind.Value} behind / stalled";

			var seconds = Math.Round((double)behind.Value / AveragePerSecond, 2);
			return $"{behind.Value} behind / {seconds:0.##}s";
		}
	}
	public string UiDetailHref =>
		$"/ui/subscriptions/{Uri.EscapeDataString(StreamId)}/{Uri.EscapeDataString(GroupName)}";
	public string UiEditHref =>
		$"{UiDetailHref}/edit";
	public string UiDeleteHref =>
		$"{UiDetailHref}/delete";
	public string UiParkedHref =>
		$"{UiDetailHref}/parked";
	public SubscriptionUpdateRequest ToUpdateRequest() =>
		new(
			StreamId,
			GroupName,
			ResolveLinkTos,
			SubscriptionStartPosition.From(StartFrom),
			MessageTimeoutMilliseconds,
			ExtraStatistics,
			MaxRetryCount,
			LiveBufferSize,
			BufferSize,
			ReadBatchSize,
			CheckPointAfterMilliseconds,
			MinCheckPointCount,
			MaxCheckPointCount,
			MaxSubscriberCount,
			string.IsNullOrWhiteSpace(NamedConsumerStrategy) ? SystemConsumerStrategies.RoundRobin : NamedConsumerStrategy);

	private static long? CalculateBehindByMessages(string streamId, string lastKnown, string checkpointed) {
		if (string.Equals(streamId, "$all", StringComparison.Ordinal))
			return string.Equals(lastKnown, checkpointed, StringComparison.Ordinal) ? 0 : null;

		if (string.IsNullOrWhiteSpace(lastKnown))
			return null;

		if (!long.TryParse(lastKnown, out var lastKnownNumber) ||
		    !long.TryParse(checkpointed, out var checkpointedNumber))
			return null;

		return Math.Max(0, lastKnownNumber - checkpointedNumber);
	}

	public static SubscriptionView From(MonitoringMessage.PersistentSubscriptionInfo info) {
		var connections = info.Connections?
			.Select(SubscriptionConnectionView.FromInfo)
			.OrderBy(x => x.ConnectionName, StringComparer.OrdinalIgnoreCase)
			.ToArray() ?? Array.Empty<SubscriptionConnectionView>();

		return new(
			info.EventSource ?? "",
			info.GroupName ?? "",
			info.Status ?? "",
			info.LastCheckpointedEventPosition ?? "",
			info.LastKnownEventPosition ?? "",
			info.StartFrom ?? "",
			info.ResolveLinktos,
			info.MessageTimeoutMilliseconds,
			info.ExtraStatistics,
			info.MaxRetryCount,
			info.LiveBufferSize,
			info.BufferSize,
			info.ReadBatchSize,
			info.CheckPointAfterMilliseconds,
			info.MinCheckPointCount,
			info.MaxCheckPointCount,
			info.NamedConsumerStrategy ?? "",
			info.AveragePerSecond,
			info.TotalItems,
			connections.Length,
			info.ParkedMessageCount,
			info.TotalInFlightMessages,
			info.OutstandingMessagesCount,
			info.ReadBufferCount,
			info.LiveBufferCount,
			info.RetryBufferCount,
			info.MaxSubscriberCount,
			connections);
	}
}

public readonly record struct SubscriptionStartPosition(string Value) {
	public static SubscriptionStartPosition From(string value) => new((value ?? "").Trim());

	public bool TryGetStreamEventNumber(out long eventNumber) =>
		long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out eventNumber);

	public bool TryGetAllStreamPosition(out TFPos position) {
		if (TryGetStreamEventNumber(out var singlePosition)) {
			position = new TFPos(singlePosition, singlePosition);
			return true;
		}

		if (TFPos.TryParse(Value, out position))
			return true;

		return TryGetPrefixedAllStreamPosition(out position) ||
		       TryGetDelimitedAllStreamPosition(out position);
	}

	private bool TryGetPrefixedAllStreamPosition(out TFPos position) {
		position = TFPos.Invalid;
		if (!Value.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
			return false;

		var separator = Value.IndexOf("/P:", StringComparison.OrdinalIgnoreCase);
		if (separator < 0)
			return false;

		return TryCreateAllStreamPosition(
			Value[2..separator],
			Value[(separator + 3)..],
			out position);
	}

	private bool TryGetDelimitedAllStreamPosition(out TFPos position) {
		position = TFPos.Invalid;
		var separator = Value.IndexOf(',', StringComparison.Ordinal);
		if (separator < 0)
			separator = Value.IndexOf(':', StringComparison.Ordinal);
		if (separator < 0)
			return false;

		return TryCreateAllStreamPosition(
			Value[..separator],
			Value[(separator + 1)..],
			out position);
	}

	private static bool TryCreateAllStreamPosition(string commit, string prepare, out TFPos position) {
		position = TFPos.Invalid;
		if (!long.TryParse(commit.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var commitPosition) ||
		    !long.TryParse(prepare.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var preparePosition))
			return false;

		position = new TFPos(commitPosition, preparePosition);
		return true;
	}
}

public sealed record SubscriptionConnectionView(
	string ConnectionName,
	string From,
	string Username,
	int AverageItemsPerSecond,
	long TotalItems,
	int AvailableSlots,
	int InFlightMessages) {
	public string DisplayName => string.IsNullOrWhiteSpace(ConnectionName) ? From : ConnectionName;

	public static SubscriptionConnectionView FromInfo(MonitoringMessage.ConnectionInfo info) =>
		new(
			info.ConnectionName ?? "",
			info.From ?? "",
			info.Username ?? "",
			info.AverageItemsPerSecond,
			info.TotalItems,
			info.AvailableSlots,
			info.InFlightMessages);
}
