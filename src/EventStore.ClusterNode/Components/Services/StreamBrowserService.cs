using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class StreamBrowserService(
	IPublisher publisher,
	IHttpContextAccessor httpContextAccessor) {
	private const int DefaultCount = 20;
	private const int MaxCount = 100;
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

	public async Task<StreamReadPage> ReadStreamBackward(
		string streamId,
		long fromEventNumber = -1,
		int count = DefaultCount,
		CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(streamId))
			return StreamReadPage.Empty("", "Enter a stream id to inspect events.");

		count = NormalizeCount(count);
		var correlationId = Guid.NewGuid();
		var envelope = new TaskCompletionEnvelope<ClientMessage.ReadStreamEventsBackwardCompleted>();

		publisher.Publish(new ClientMessage.ReadStreamEventsBackward(
			Guid.NewGuid(),
			correlationId,
			envelope,
			streamId.Trim(),
			fromEventNumber,
			count,
			resolveLinkTos: true,
			requireLeader: false,
			validationStreamVersion: null,
			CurrentUser,
			cancellationToken: cancellationToken));

		ClientMessage.ReadStreamEventsBackwardCompleted completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return StreamReadPage.Empty(streamId, $"Timed out reading '{streamId}'.");
		}

		return completed.Result switch {
			ReadStreamResult.Success => StreamReadPage.Success(
				completed.EventStreamId,
				completed.FromEventNumber,
				completed.NextEventNumber,
				completed.LastEventNumber,
				completed.IsEndOfStream,
				completed.Events.ToViewEvents()),
			ReadStreamResult.NoStream => StreamReadPage.Empty(streamId, $"Stream '{streamId}' was not found."),
			ReadStreamResult.StreamDeleted => StreamReadPage.Empty(streamId, $"Stream '{streamId}' has been deleted."),
			ReadStreamResult.AccessDenied => StreamReadPage.Empty(streamId, $"Read access was denied for '{streamId}'."),
			_ => StreamReadPage.Empty(streamId, string.IsNullOrWhiteSpace(completed.Error)
				? $"Unable to read '{streamId}'. Result: {completed.Result}."
				: completed.Error)
		};
	}

	public async Task<RecentEventsPage> ReadRecentEvents(
		int count = 12,
		CancellationToken cancellationToken = default) {
		count = NormalizeCount(count);
		var correlationId = Guid.NewGuid();
		var envelope = new TaskCompletionEnvelope<ClientMessage.ReadAllEventsBackwardCompleted>();
		var head = TFPos.HeadOfTf;

		publisher.Publish(new ClientMessage.ReadAllEventsBackward(
			Guid.NewGuid(),
			correlationId,
			envelope,
			head.CommitPosition,
			head.PreparePosition,
			count,
			resolveLinkTos: true,
			requireLeader: false,
			validationTfLastCommitPosition: null,
			CurrentUser,
			cancellationToken: cancellationToken));

		ClientMessage.ReadAllEventsBackwardCompleted completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return RecentEventsPage.Unavailable("Timed out reading recent events.");
		}

		return completed.Result switch {
			ReadAllResult.Success => RecentEventsPage.Success(completed.Events.ToViewEvents()),
			ReadAllResult.AccessDenied => RecentEventsPage.Unavailable("Read access was denied for recent events."),
			_ => RecentEventsPage.Unavailable(string.IsNullOrWhiteSpace(completed.Error)
				? $"Unable to read recent events. Result: {completed.Result}."
				: completed.Error)
		};
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private static int NormalizeCount(int count) =>
		Math.Clamp(count <= 0 ? DefaultCount : count, 1, MaxCount);

	private sealed class TaskCompletionEnvelope<T> : IEnvelope where T : Message {
		private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<T> Task => _source.Task;

		public void ReplyWith<U>(U message) where U : Message {
			if (message is T typed) {
				_source.TrySetResult(typed);
				return;
			}

			_source.TrySetException(new InvalidOperationException(
				$"Expected {typeof(T).Name} but received {message.GetType().Name}."));
		}
	}
}

public sealed record StreamReadPage(
	string StreamId,
	long FromEventNumber,
	long NextEventNumber,
	long LastEventNumber,
	bool IsEndOfStream,
	IReadOnlyList<StreamViewEvent> Events,
	string Message) {
	public bool HasEvents => Events.Count > 0;
	public bool CanReadOlder => HasEvents && NextEventNumber >= 0 && !IsEndOfStream;

	public static StreamReadPage Success(
		string streamId,
		long fromEventNumber,
		long nextEventNumber,
		long lastEventNumber,
		bool isEndOfStream,
		IReadOnlyList<StreamViewEvent> events) =>
		new(streamId, fromEventNumber, nextEventNumber, lastEventNumber, isEndOfStream, events, "");

	public static StreamReadPage Empty(string streamId, string message) =>
		new(streamId, -1, -1, -1, true, Array.Empty<StreamViewEvent>(), message);
}

public sealed record RecentEventsPage(
	IReadOnlyList<StreamViewEvent> Events,
	string Message) {
	public bool HasEvents => Events.Count > 0;
	public static RecentEventsPage Success(IReadOnlyList<StreamViewEvent> events) => new(events, "");
	public static RecentEventsPage Unavailable(string message) => new(Array.Empty<StreamViewEvent>(), message);
}

public sealed record StreamViewEvent(
	string StreamId,
	long EventNumber,
	string EventType,
	Guid EventId,
	DateTime TimeStamp,
	string Data,
	string Metadata,
	bool IsJson,
	long? CommitPosition,
	long? PreparePosition) {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true
	};

	public string DataForDisplay => FormatBody(Data, IsJson);
	public string MetadataForDisplay => FormatBody(Metadata, true);
	public string DetailHref => $"/ui/streams/{Uri.EscapeDataString(StreamId)}?from={EventNumber}&count=1";
	public string RawHref => $"/streams/{Uri.EscapeDataString(StreamId)}/{EventNumber}?embed=tryharder";

	private static string FormatBody(string value, bool preferJson) {
		if (string.IsNullOrWhiteSpace(value))
			return "";

		if (!preferJson)
			return value;

		try {
			using var document = JsonDocument.Parse(value);
			return JsonSerializer.Serialize(document.RootElement, JsonOptions);
		} catch (JsonException) {
			return value;
		}
	}
}

file static class StreamBrowserMapping {
	public static IReadOnlyList<StreamViewEvent> ToViewEvents(this IReadOnlyList<ResolvedEvent> events) {
		var result = new List<StreamViewEvent>(events.Count);
		foreach (var resolvedEvent in events) {
			var record = resolvedEvent.Event ?? resolvedEvent.Link;
			if (record is null)
				continue;

			var position = resolvedEvent.OriginalPosition;
			result.Add(new StreamViewEvent(
				record.EventStreamId,
				record.EventNumber,
				record.EventType,
				record.EventId,
				record.TimeStamp,
				Decode(record.Data),
				Decode(record.Metadata),
				record.IsJson,
				position?.CommitPosition,
				position?.PreparePosition));
		}

		return result;
	}

	private static string Decode(ReadOnlyMemory<byte> data) =>
		data.IsEmpty ? "" : Encoding.UTF8.GetString(data.Span);
}
