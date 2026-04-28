using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class StreamBrowserService(
	IPublisher publisher,
	IHttpContextAccessor httpContextAccessor) {
	private const int DefaultCount = 20;
	private const int MaxCount = 100;
	private const int RecentStreamCount = 50;
	private const int RecentChangeCount = 100;
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

	public async Task<StreamOverviewPage> ReadOverview(CancellationToken cancellationToken = default) {
		var created = await ReadStreamBackward(SystemStreams.StreamsStream, count: RecentStreamCount, cancellationToken: cancellationToken);
		var changed = await ReadRecentEvents(RecentChangeCount, cancellationToken);

		return StreamOverviewPage.Create(
			BuildCreatedStreams(created),
			BuildChangedStreams(changed),
			created.Message,
			changed.Message);
	}

	public async Task<StreamReadPage> ReadStreamBackward(
		string streamId,
		long fromEventNumber = -1,
		int count = DefaultCount,
		CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(streamId))
			return StreamReadPage.Empty("", "Enter a stream id to inspect events.");

		count = NormalizeCount(count);
		fromEventNumber = NormalizeFromEventNumber(fromEventNumber);
		var correlationId = Guid.NewGuid();
		var envelope = new TaskCompletionEnvelope<ClientMessage.ReadStreamEventsBackwardCompleted>();

		ClientMessage.ReadStreamEventsBackwardCompleted completed;
		try {
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
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return StreamReadPage.Empty(streamId, $"Timed out reading '{streamId}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return StreamReadPage.Empty(streamId, $"Unable to read '{streamId}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ReadStreamResult.Success => StreamReadPage.Success(
				completed.EventStreamId,
				StreamReadDirection.Backward,
				completed.FromEventNumber,
				completed.NextEventNumber,
				completed.LastEventNumber,
				completed.IsEndOfStream,
				count,
				completed.Events.ToViewEvents()),
			ReadStreamResult.NoStream => StreamReadPage.Empty(streamId, $"Stream '{streamId}' was not found."),
			ReadStreamResult.StreamDeleted => StreamReadPage.Empty(streamId, $"Stream '{streamId}' has been deleted."),
			ReadStreamResult.AccessDenied => StreamReadPage.Empty(streamId, $"Read access was denied for '{streamId}'."),
			_ => StreamReadPage.Empty(streamId, string.IsNullOrWhiteSpace(completed.Error)
				? $"Unable to read '{streamId}'. Result: {completed.Result}."
				: completed.Error)
		};
	}

	public async Task<StreamReadPage> ReadStreamForward(
		string streamId,
		long fromEventNumber = 0,
		int count = DefaultCount,
		CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(streamId))
			return StreamReadPage.Empty("", "Enter a stream id to inspect events.");

		count = NormalizeCount(count);
		fromEventNumber = Math.Max(fromEventNumber, 0);
		var correlationId = Guid.NewGuid();
		var envelope = new TaskCompletionEnvelope<ClientMessage.ReadStreamEventsForwardCompleted>();

		ClientMessage.ReadStreamEventsForwardCompleted completed;
		try {
			publisher.Publish(new ClientMessage.ReadStreamEventsForward(
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
				replyOnExpired: false,
				cancellationToken: cancellationToken));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return StreamReadPage.Empty(streamId, $"Timed out reading '{streamId}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return StreamReadPage.Empty(streamId, $"Unable to read '{streamId}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ReadStreamResult.Success => StreamReadPage.Success(
				completed.EventStreamId,
				StreamReadDirection.Forward,
				completed.FromEventNumber,
				completed.NextEventNumber,
				completed.LastEventNumber,
				completed.IsEndOfStream,
				count,
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

		ClientMessage.ReadAllEventsBackwardCompleted completed;
		try {
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
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return RecentEventsPage.Unavailable("Timed out reading recent events.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return RecentEventsPage.Unavailable($"Unable to read recent events: {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ReadAllResult.Success => RecentEventsPage.Success(completed.Events.ToViewEvents()),
			ReadAllResult.AccessDenied => RecentEventsPage.Unavailable("Read access was denied for recent events."),
			_ => RecentEventsPage.Unavailable(string.IsNullOrWhiteSpace(completed.Error)
				? $"Unable to read recent events. Result: {completed.Result}."
				: completed.Error)
		};
	}

	public async Task<StreamEventDetailPage> ReadEvent(
		string streamId,
		long eventNumber,
		CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(streamId))
			return StreamEventDetailPage.Unavailable("", eventNumber, "Enter a stream id to inspect an event.");

		if (eventNumber < 0)
			return StreamEventDetailPage.Unavailable(streamId, eventNumber, "Event number must be zero or greater.");

		var correlationId = Guid.NewGuid();
		var envelope = new TaskCompletionEnvelope<ClientMessage.ReadEventCompleted>();

		ClientMessage.ReadEventCompleted completed;
		try {
			publisher.Publish(new ClientMessage.ReadEvent(
				Guid.NewGuid(),
				correlationId,
				envelope,
				streamId.Trim(),
				eventNumber,
				resolveLinkTos: true,
				requireLeader: false,
				CurrentUser,
				cancellationToken: cancellationToken));
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return StreamEventDetailPage.Unavailable(streamId, eventNumber, $"Timed out reading event #{eventNumber}.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return StreamEventDetailPage.Unavailable(streamId, eventNumber, $"Unable to read event #{eventNumber}: {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ReadEventResult.Success when completed.Record.ToViewEvent() is { } ev =>
				StreamEventDetailPage.Success(ev),
			ReadEventResult.NotFound or ReadEventResult.NoStream =>
				StreamEventDetailPage.Unavailable(streamId, eventNumber, $"Event #{eventNumber} was not found in '{streamId}'."),
			ReadEventResult.StreamDeleted =>
				StreamEventDetailPage.Unavailable(streamId, eventNumber, $"Stream '{streamId}' has been deleted."),
			ReadEventResult.AccessDenied =>
				StreamEventDetailPage.Unavailable(streamId, eventNumber, $"Read access was denied for '{streamId}'."),
			_ => StreamEventDetailPage.Unavailable(streamId, eventNumber, string.IsNullOrWhiteSpace(completed.Error)
				? $"Unable to read event #{eventNumber}. Result: {completed.Result}."
				: completed.Error)
		};
	}

	public async Task<StreamAclPage> ReadAcl(
		string streamId,
		CancellationToken cancellationToken = default) {
		var validation = ValidateUserStream(streamId);
		if (!validation.Success)
			return StreamAclPage.Unavailable(streamId, validation.Message);

		var metadataStream = SystemStreams.MetastreamOf(streamId.Trim());
		var metadata = await ReadStreamBackward(metadataStream, count: 1, cancellationToken: cancellationToken);
		if (!metadata.HasEvents && string.IsNullOrWhiteSpace(metadata.Message))
			return StreamAclPage.Success(streamId.Trim(), ExpectedVersion.NoStream, "", StreamAclInput.Empty(streamId.Trim()), "No stream metadata exists yet.");
		if (!metadata.HasEvents && metadata.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
			return StreamAclPage.Success(streamId.Trim(), ExpectedVersion.NoStream, "", StreamAclInput.Empty(streamId.Trim()), "No stream metadata exists yet.");
		if (!metadata.HasEvents)
			return StreamAclPage.Unavailable(streamId.Trim(), metadata.Message);

		var latestMetadata = metadata.Events[0];
		var rawMetadata = latestMetadata.Data;
		return StreamAclPage.Success(streamId.Trim(), latestMetadata.EventNumber, rawMetadata, StreamAclInput.FromMetadata(streamId.Trim(), rawMetadata), "");
	}

	public async Task<StreamCommandResult> AppendEvent(
		StreamAppendRequest request,
		CancellationToken cancellationToken = default) {
		var validation = ValidateAppend(request);
		if (!validation.Success)
			return validation;

		var streamId = request.StreamId.Trim();
		var eventId = string.IsNullOrWhiteSpace(request.EventId)
			? Guid.NewGuid()
			: Guid.Parse(request.EventId.Trim());
		var data = NormalizeJson(request.Data);
		var metadata = NormalizeJson(request.Metadata);
		var @event = new Event(eventId, request.EventType.Trim(), isJson: true, data, metadata);
		var completed = await Write(
			envelope => new ClientMessage.WriteEvents(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				requireLeader: false,
				streamId,
				ExpectedVersion.Any,
				[@event],
				CurrentUser,
				cancellationToken: cancellationToken),
			$"Timed out appending event to '{streamId}'.",
			$"Unable to append event to '{streamId}'",
			cancellationToken);

		return completed.Result == OperationResult.Success
			? StreamCommandResult.Succeeded(streamId, $"Event '{eventId}' was appended to '{streamId}'.")
			: StreamCommandResult.Failure(streamId, FriendlyWriteError(completed.Result, completed.Message));
	}

	public async Task<StreamCommandResult> UpdateAcl(
		StreamAclUpdateRequest request,
		CancellationToken cancellationToken = default) {
		var validation = ValidateUserStream(request.StreamId);
		if (!validation.Success)
			return validation;

		var streamId = request.StreamId.Trim();
		var metadata = await ReadAcl(streamId, cancellationToken);
		if (!metadata.IsAvailable)
			return StreamCommandResult.Failure(streamId, metadata.Message);

		var payload = MergeAcl(metadata.RawMetadata, request);
		var @event = new Event(Guid.NewGuid(), SystemEventTypes.StreamMetadata, isJson: true, payload, null);
		var completed = await Write(
			envelope => new ClientMessage.WriteEvents(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				requireLeader: false,
				SystemStreams.MetastreamOf(streamId),
				metadata.MetadataVersion,
				[@event],
				CurrentUser,
				cancellationToken: cancellationToken),
			$"Timed out updating metadata for '{streamId}'.",
			$"Unable to update metadata for '{streamId}'",
			cancellationToken);

		return completed.Result == OperationResult.Success
			? StreamCommandResult.Succeeded(streamId, $"ACL metadata for '{streamId}' was updated.")
			: StreamCommandResult.Failure(streamId, FriendlyWriteError(completed.Result, completed.Message));
	}

	public async Task<StreamCommandResult> DeleteStream(
		string streamId,
		bool hardDelete,
		CancellationToken cancellationToken = default) {
		var validation = ValidateUserStream(streamId);
		if (!validation.Success)
			return validation;

		streamId = streamId.Trim();
		var completed = await Delete(
			envelope => new ClientMessage.DeleteStream(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				requireLeader: false,
				streamId,
				ExpectedVersion.Any,
				hardDelete,
				CurrentUser,
				cancellationToken: cancellationToken),
			$"Timed out deleting '{streamId}'.",
			$"Unable to delete '{streamId}'",
			cancellationToken);

		return completed.Result == OperationResult.Success
			? StreamCommandResult.Succeeded(streamId, hardDelete
				? $"Stream '{streamId}' was hard deleted."
				: $"Stream '{streamId}' was deleted.")
			: StreamCommandResult.Failure(streamId, FriendlyWriteError(completed.Result, completed.Message));
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private static int NormalizeCount(int count) =>
		Math.Clamp(count <= 0 ? DefaultCount : count, 1, MaxCount);

	private static long NormalizeFromEventNumber(long fromEventNumber) =>
		Math.Max(fromEventNumber, -1);

	private async Task<ClientMessage.WriteEventsCompleted> Write(
		Func<IEnvelope, ClientMessage.WriteEvents> createMessage,
		string timeoutMessage,
		string errorPrefix,
		CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<ClientMessage.WriteEventsCompleted>();
		try {
			publisher.Publish(createMessage(envelope));
			return await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException ex) {
			return new ClientMessage.WriteEventsCompleted(Guid.NewGuid(), OperationResult.PrepareTimeout, timeoutMessage ?? ex.Message);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return new ClientMessage.WriteEventsCompleted(Guid.NewGuid(), OperationResult.PrepareTimeout, $"{errorPrefix}: {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ClientMessage.DeleteStreamCompleted> Delete(
		Func<IEnvelope, ClientMessage.DeleteStream> createMessage,
		string timeoutMessage,
		string errorPrefix,
		CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<ClientMessage.DeleteStreamCompleted>();
		try {
			publisher.Publish(createMessage(envelope));
			return await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException ex) {
			return new ClientMessage.DeleteStreamCompleted(Guid.NewGuid(), OperationResult.PrepareTimeout, timeoutMessage ?? ex.Message);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return new ClientMessage.DeleteStreamCompleted(Guid.NewGuid(), OperationResult.PrepareTimeout, $"{errorPrefix}: {UiMessages.Friendly(ex)}");
		}
	}

	private static IReadOnlyList<StreamSummaryItem> BuildCreatedStreams(StreamReadPage page) =>
		page.Events
			.Select(x => new StreamSummaryItem(StreamReferenceToStreamId(x), x.EventType, x.TimeStamp, x.ResolvedEventNumber))
			.Where(x => !string.IsNullOrWhiteSpace(x.StreamId))
			.DistinctBy(x => x.StreamId, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(x => x.UpdatedUtc)
			.ToArray();

	private static IReadOnlyList<StreamSummaryItem> BuildChangedStreams(RecentEventsPage page) =>
		page.Events
			.Select(x => new StreamSummaryItem(x.ResolvedStreamId, x.EventType, x.TimeStamp, x.ResolvedEventNumber))
			.Where(x => !string.IsNullOrWhiteSpace(x.StreamId))
			.Where(x => !SystemStreams.IsSystemStream(x.StreamId))
			.DistinctBy(x => x.StreamId, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(x => x.UpdatedUtc)
			.ToArray();

	private static string StreamReferenceToStreamId(StreamViewEvent ev) {
		try {
			return SystemEventTypes.StreamReferenceEventToStreamId(ev.EventType, ev.Data);
		} catch (Exception) {
			return ev.ResolvedStreamId;
		}
	}

	private static StreamCommandResult ValidateAppend(StreamAppendRequest request) {
		var streamValidation = ValidateWritableStream(request.StreamId);
		if (!streamValidation.Success)
			return streamValidation;

		if (!string.IsNullOrWhiteSpace(request.EventId) && !Guid.TryParse(request.EventId, out _))
			return StreamCommandResult.Failure(request.StreamId, "Enter a valid event id.");

		if (string.IsNullOrWhiteSpace(request.EventType))
			return StreamCommandResult.Failure(request.StreamId, "Enter an event type.");

		if (!IsJson(request.Data))
			return StreamCommandResult.Failure(request.StreamId, "Event data must be valid JSON.");

		if (!IsJson(request.Metadata))
			return StreamCommandResult.Failure(request.StreamId, "Event metadata must be valid JSON.");

		return StreamCommandResult.Succeeded(request.StreamId.Trim(), "");
	}

	private static StreamCommandResult ValidateWritableStream(string streamId) {
		if (string.IsNullOrWhiteSpace(streamId))
			return StreamCommandResult.Failure("", "Enter a stream id.");

		streamId = streamId.Trim();
		return SystemStreams.IsSystemStream(streamId)
			? StreamCommandResult.Failure(streamId, "System streams cannot be written from the stream browser.")
			: StreamCommandResult.Succeeded(streamId, "");
	}

	private static StreamCommandResult ValidateUserStream(string streamId) {
		if (string.IsNullOrWhiteSpace(streamId))
			return StreamCommandResult.Failure("", "Enter a stream id.");

		streamId = streamId.Trim();
		if (string.Equals(streamId, SystemStreams.AllStream, StringComparison.OrdinalIgnoreCase))
			return StreamCommandResult.Failure(streamId, "$all cannot be edited or deleted.");

		if (SystemStreams.IsSystemStream(streamId))
			return StreamCommandResult.Failure(streamId, "System streams cannot be edited or deleted from the stream browser.");

		return SystemStreams.IsMetastream(streamId)
			? StreamCommandResult.Failure(streamId, "Metadata streams cannot be edited or deleted directly.")
			: StreamCommandResult.Succeeded(streamId, "");
	}

	private static bool IsJson(string value) {
		if (string.IsNullOrWhiteSpace(value))
			return true;

		try {
			JsonNode.Parse(value);
			return true;
		} catch (JsonException) {
			return false;
		}
	}

	private static string NormalizeJson(string value) =>
		string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

	private static string MergeAcl(string rawMetadata, StreamAclUpdateRequest request) {
		var metadata = ParseMetadataObject(rawMetadata);
		metadata[SystemMetadata.Acl] = new JsonObject {
			[SystemMetadata.AclRead] = RolesNode(request.ReadRoles),
			[SystemMetadata.AclWrite] = RolesNode(request.WriteRoles),
			[SystemMetadata.AclDelete] = RolesNode(request.DeleteRoles),
			[SystemMetadata.AclMetaRead] = RolesNode(request.MetaReadRoles),
			[SystemMetadata.AclMetaWrite] = RolesNode(request.MetaWriteRoles)
		};

		RemoveNullAclProperties(metadata);
		return metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
	}

	private static JsonObject ParseMetadataObject(string rawMetadata) {
		if (string.IsNullOrWhiteSpace(rawMetadata))
			return new JsonObject();

		return JsonNode.Parse(rawMetadata) as JsonObject ?? new JsonObject();
	}

	private static JsonNode RolesNode(string value) {
		var roles = SplitRoles(value);
		return roles.Length switch {
			0 => null,
			1 => JsonValue.Create(roles[0]),
			_ => new JsonArray(roles.Select(role => (JsonNode)JsonValue.Create(role)).ToArray())
		};
	}

	private static string[] SplitRoles(string value) =>
		(value ?? "")
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static void RemoveNullAclProperties(JsonObject metadata) {
		if (metadata[SystemMetadata.Acl] is not JsonObject acl)
			return;

		foreach (var key in acl.Where(x => x.Value is null).Select(x => x.Key).ToArray())
			acl.Remove(key);
	}

	private static string FriendlyWriteError(OperationResult result, string message) =>
		string.IsNullOrWhiteSpace(message)
			? result switch {
				OperationResult.AccessDenied => "Access was denied.",
				OperationResult.StreamDeleted => "The stream has been deleted.",
				OperationResult.WrongExpectedVersion => "The stream version changed before the command completed.",
				OperationResult.PrepareTimeout or OperationResult.CommitTimeout or OperationResult.ForwardTimeout => "The command timed out.",
				_ => $"The command failed with {result}."
			}
			: message;

}

public enum StreamReadDirection {
	Backward,
	Forward
}

public sealed record StreamReadPage(
	string StreamId,
	StreamReadDirection Direction,
	long FromEventNumber,
	long NextEventNumber,
	long LastEventNumber,
	bool IsEndOfStream,
	int Count,
	IReadOnlyList<StreamViewEvent> Events,
	string Message) {
	private const int EmptyPageCount = 20;

	public bool HasEvents => Events.Count > 0;
	public bool CanReadNextPage => HasEvents && NextEventNumber >= 0 && !IsEndOfStream;
	public string NextPageLabel => Direction == StreamReadDirection.Forward ? "Newer events" : "Older events";
	public string LatestHref => $"/ui/streams/{Uri.EscapeDataString(StreamId)}";
	public string FirstPageHref => $"/ui/streams/{Uri.EscapeDataString(StreamId)}?direction=forward&from=0&count={Count}";
	public string NextPageHref => Direction == StreamReadDirection.Forward
		? $"/ui/streams/{Uri.EscapeDataString(StreamId)}?direction=forward&from={NextEventNumber}&count={Count}"
		: $"/ui/streams/{Uri.EscapeDataString(StreamId)}?from={NextEventNumber}&count={Count}";

	public static StreamReadPage Success(
		string streamId,
		StreamReadDirection direction,
		long fromEventNumber,
		long nextEventNumber,
		long lastEventNumber,
		bool isEndOfStream,
		int count,
		IReadOnlyList<StreamViewEvent> events) =>
		new(streamId, direction, fromEventNumber, nextEventNumber, lastEventNumber, isEndOfStream, count, events, "");

	public static StreamReadPage Empty(string streamId, string message) =>
		new(streamId, StreamReadDirection.Backward, -1, -1, -1, true, EmptyPageCount, Array.Empty<StreamViewEvent>(), message);
}

public sealed record RecentEventsPage(
	IReadOnlyList<StreamViewEvent> Events,
	string Message) {
	public bool HasEvents => Events.Count > 0;
	public static RecentEventsPage Success(IReadOnlyList<StreamViewEvent> events) => new(events, "");
	public static RecentEventsPage Unavailable(string message) => new(Array.Empty<StreamViewEvent>(), message);
}

public sealed record StreamOverviewPage(
	IReadOnlyList<StreamSummaryItem> RecentlyCreated,
	IReadOnlyList<StreamSummaryItem> RecentlyChanged,
	string CreatedMessage,
	string ChangedMessage) {
	public bool HasRecentlyCreated => RecentlyCreated.Count > 0;
	public bool HasRecentlyChanged => RecentlyChanged.Count > 0;

	public static StreamOverviewPage Create(
		IReadOnlyList<StreamSummaryItem> recentlyCreated,
		IReadOnlyList<StreamSummaryItem> recentlyChanged,
		string createdMessage,
		string changedMessage) =>
		new(recentlyCreated, recentlyChanged, createdMessage, changedMessage);
}

public sealed record StreamSummaryItem(
	string StreamId,
	string EventType,
	DateTime UpdatedUtc,
	long EventNumber) {
	public string DetailHref => $"/ui/streams/{Uri.EscapeDataString(StreamId)}";
	public string UpdatedLabel => UpdatedUtc.ToString("u");
}

public sealed record StreamEventDetailPage(
	StreamViewEvent Event,
	string StreamId,
	long EventNumber,
	string Message) {
	public bool HasEvent => Event is not null;
	public string PreviousHref => EventNumber > 0 ? $"/ui/streams/event/{EventNumber - 1}/{Uri.EscapeDataString(StreamId)}" : "";
	public string NextHref => HasEvent ? $"/ui/streams/event/{EventNumber + 1}/{Uri.EscapeDataString(StreamId)}" : "";
	public string BackHref => $"/ui/streams/{Uri.EscapeDataString(StreamId)}";

	public static StreamEventDetailPage Success(StreamViewEvent ev) => new(ev, ev.StreamId, ev.EventNumber, "");
	public static StreamEventDetailPage Unavailable(string streamId, long eventNumber, string message) =>
		new(null, streamId, eventNumber, message);
}

public sealed record StreamAclPage(
	string StreamId,
	long MetadataVersion,
	string RawMetadata,
	StreamAclInput Input,
	string Message) {
	public bool IsAvailable => Input is not null;
	public bool HasMetadata => !string.IsNullOrWhiteSpace(RawMetadata);

	public static StreamAclPage Success(string streamId, long metadataVersion, string rawMetadata, StreamAclInput input, string message) =>
		new(streamId, metadataVersion, rawMetadata, input, message);

	public static StreamAclPage Unavailable(string streamId, string message) =>
		new(streamId, ExpectedVersion.Any, "", null, message);
}

public sealed record StreamAclInput(
	string StreamId,
	string ReadRoles,
	string WriteRoles,
	string DeleteRoles,
	string MetaReadRoles,
	string MetaWriteRoles) {
	public static StreamAclInput Empty(string streamId) => new(streamId, "", "", "", "", "");

	public static StreamAclInput FromMetadata(string streamId, string rawMetadata) {
		if (string.IsNullOrWhiteSpace(rawMetadata))
			return Empty(streamId);

		try {
			var acl = JsonNode.Parse(rawMetadata)?[SystemMetadata.Acl];
			return new StreamAclInput(
				streamId,
				ParseRolesNode(acl?[SystemMetadata.AclRead]),
				ParseRolesNode(acl?[SystemMetadata.AclWrite]),
				ParseRolesNode(acl?[SystemMetadata.AclDelete]),
				ParseRolesNode(acl?[SystemMetadata.AclMetaRead]),
				ParseRolesNode(acl?[SystemMetadata.AclMetaWrite]));
		} catch (JsonException) {
			return Empty(streamId);
		}
	}

	private static string ParseRolesNode(JsonNode node) =>
		node switch {
			JsonArray array => string.Join(", ", array.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x))),
			JsonValue value when value.TryGetValue<string>(out var role) => role,
			_ => ""
		};
}

public sealed record StreamCommandResult(
	bool Success,
	string StreamId,
	string Message) {
	public static StreamCommandResult Succeeded(string streamId, string message) => new(true, streamId, message);
	public static StreamCommandResult Failure(string streamId, string message) => new(false, streamId, message);
}

public sealed record StreamAppendRequest(
	string StreamId,
	string EventId,
	string EventType,
	string Data,
	string Metadata);

public sealed record StreamAclUpdateRequest(
	string StreamId,
	string ReadRoles,
	string WriteRoles,
	string DeleteRoles,
	string MetaReadRoles,
	string MetaWriteRoles);

public sealed record StreamViewEvent(
	string StreamId,
	long EventNumber,
	string ResolvedStreamId,
	long ResolvedEventNumber,
	string EventType,
	Guid EventId,
	DateTime TimeStamp,
	string Data,
	string Metadata,
	string LinkMetadata,
	bool IsJson,
	long? CommitPosition,
	long? PreparePosition) {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true
	};

	public string DataForDisplay => FormatBody(Data, IsJson);
	public string MetadataForDisplay => FormatBody(Metadata, true);
	public string LinkMetadataForDisplay => FormatBody(LinkMetadata, true);
	public bool HasLinkMetadata => !string.IsNullOrWhiteSpace(LinkMetadata);
	public string DetailHref => $"/ui/streams/event/{EventNumber}/{Uri.EscapeDataString(StreamId)}";
	public string AppendLikeHref => $"/ui/streams/append/{Uri.EscapeDataString(StreamId)}?fromEvent={EventNumber}";
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
	public static StreamViewEvent ToViewEvent(this ResolvedEvent resolvedEvent) {
		var record = resolvedEvent.Event ?? resolvedEvent.Link;
		if (record is null)
			return null;

		var position = resolvedEvent.OriginalPosition;
		var identity = resolvedEvent.Link ?? record;
		return new StreamViewEvent(
			identity.EventStreamId,
			identity.EventNumber,
			record.EventStreamId,
			record.EventNumber,
			record.EventType,
			record.EventId,
			record.TimeStamp,
			Decode(record.Data),
			Decode(record.Metadata),
			resolvedEvent.Link is null ? "" : Decode(resolvedEvent.Link.Metadata),
			record.IsJson,
			position?.CommitPosition,
			position?.PreparePosition);
	}

	public static IReadOnlyList<StreamViewEvent> ToViewEvents(this IReadOnlyList<ResolvedEvent> events) {
		var result = new List<StreamViewEvent>(events.Count);
		foreach (var resolvedEvent in events) {
			var viewEvent = resolvedEvent.ToViewEvent();
			if (viewEvent is null)
				continue;

			result.Add(viewEvent);
		}

		return result;
	}

	private static string Decode(ReadOnlyMemory<byte> data) =>
		data.IsEmpty ? "" : Encoding.UTF8.GetString(data.Span);
}
