using System;
using System.Threading.Tasks;
using EventStore.Client.Projections;
using EventStore.Common.Utils;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Messages.EventReaders.Feeds;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.Checkpointing;
using Grpc.Core;
using Newtonsoft.Json.Linq;

namespace EventStore.Projections.Core.Services.Grpc;

internal partial class ProjectionManagement
{
	private static readonly Operation DebugProjectionOperation = new Operation(Operations.Projections.DebugProjection);

	public override async Task<ReadEventsResp> ReadEvents(ReadEventsReq request, ServerCallContext context)
	{
		var readSource = new TaskCompletionSource<FeedReaderMessage.FeedPage>();
		using var cancellationRegistration =
			context.CancellationToken.Register(() => readSource.TrySetCanceled(context.CancellationToken));
		var options = request.Options;
		var user = context.GetHttpContext().User;

		if (!await _authorizationProvider.CheckAccessAsync(user, DebugProjectionOperation, context.CancellationToken))
		{
			throw RpcExceptions.AccessDenied();
		}

		var querySource = ParseQuerySources(options.QuerySourcesJson);
		var fromPosition = ParseCheckpointTag(options.PositionJson);
		var maxEvents = options.MaxEvents > 0 ? options.MaxEvents : 10;

		var envelope = new CallbackEnvelope(OnMessage);
		_publisher.Publish(new FeedReaderMessage.ReadPage(
			Guid.NewGuid(),
			envelope,
			user,
			querySource,
			fromPosition,
			maxEvents));

		var page = await readSource.Task;
		if (page.Error == FeedReaderMessage.FeedPage.ErrorStatus.NotAuthorized)
		{
			throw RpcExceptions.AccessDenied();
		}

		var details = new ReadEventsResp.Types.Details {
			CorrelationId = page.CorrelationId.ToString("D"),
			ReaderPositionJson = page.LastReaderPosition.ToJsonRaw().ToString()
		};

		foreach (var taggedEvent in page.Events)
		{
			var resolvedEvent = taggedEvent.ResolvedEvent;
			details.Events.Add(new ReadEventsResp.Types.Details.Types.Event {
				EventStreamId = resolvedEvent.EventStreamId ?? string.Empty,
				EventNumber = resolvedEvent.EventSequenceNumber,
				EventType = resolvedEvent.EventType ?? string.Empty,
				Data = GetProtoValue(resolvedEvent.Data, resolvedEvent.IsJson),
				Metadata = GetProtoValue(resolvedEvent.Metadata, resolvedEvent.IsJson),
				LinkMetadata = GetProtoValue(resolvedEvent.PositionMetadata, resolvedEvent.IsJson),
				IsJson = resolvedEvent.IsJson,
				ReaderPositionJson = taggedEvent.ReaderPosition.ToJsonRaw().ToString()
			});
		}

		return new ReadEventsResp {
			Details = details
		};

		void OnMessage(Message message)
		{
			if (message is not FeedReaderMessage.FeedPage page)
			{
				readSource.TrySetException(UnknownMessage<FeedReaderMessage.FeedPage>(message));
				return;
			}

			readSource.TrySetResult(page);
		}
	}

	private static QuerySourcesDefinition ParseQuerySources(string querySourcesJson)
	{
		if (string.IsNullOrWhiteSpace(querySourcesJson))
		{
			throw RpcExceptions.InvalidArgument("A projection query source definition is required.");
		}

		try
		{
			var parsed = querySourcesJson.ParseJson<QuerySourcesDefinition>();
			return parsed ?? throw RpcExceptions.InvalidArgument("A projection query source definition is required.");
		}
		catch (Exception ex) when (ex is not RpcException)
		{
			throw RpcExceptions.InvalidArgument($"Failed to parse query source definition: {ex.Message}");
		}
	}

	private static CheckpointTag ParseCheckpointTag(string positionJson)
	{
		if (string.IsNullOrWhiteSpace(positionJson))
		{
			throw RpcExceptions.InvalidArgument("A projection checkpoint position is required.");
		}

		try
		{
			using var tokenReader = new JTokenReader(JToken.Parse(positionJson));
			return CheckpointTag.FromJson(tokenReader, new ProjectionVersion(0, 0, 0)).Tag;
		}
		catch (Exception ex) when (ex is not RpcException)
		{
			throw RpcExceptions.InvalidArgument($"Failed to parse checkpoint position: {ex.Message}");
		}
	}
}
