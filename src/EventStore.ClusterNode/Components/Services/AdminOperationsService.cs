using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class AdminOperationsService(
	IPublisher publisher,
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor,
	ClusterVNodeHostedService hostedService) {
	private const int ScavengeHistoryCount = 100;
	private const int ScavengeDetailPageSize = 20;
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation SubsystemsOperation = new(Operations.Node.Information.Subsystems);
	private static readonly Operation ScavengeReadOperation = new(Operations.Node.Scavenge.Read);
	private static readonly Operation ScavengeStartOperation = new(Operations.Node.Scavenge.Start);
	private static readonly Operation ScavengeStopOperation = new(Operations.Node.Scavenge.Stop);
	private static readonly Operation ShutdownOperation = new(Operations.Node.Shutdown);
	private static readonly Operation ReloadConfigOperation = new(Operations.Node.ReloadConfiguration);
	private static readonly Operation MergeIndexesOperation = new(Operations.Node.MergeIndexes);
	private static readonly Operation ResignOperation = new(Operations.Node.Resign);
	private static readonly Operation SetPriorityOperation = new(Operations.Node.SetPriority);

	public async Task<AdminOperationsPage> Read(CancellationToken cancellationToken = default) {
		var subsystems = await ReadSubsystems(cancellationToken);

		if (!hostedService.SupportsScavenge) {
			var unavailable = ScavengeStatusView.Unavailable(hostedService.ScavengeSupportMessage);
			return new AdminOperationsPage(
				subsystems,
				new ScavengeOperationsPanel(
					false,
					hostedService.ScavengeSupportMessage,
					unavailable,
					unavailable,
					Array.Empty<ScavengeHistoryItem>(),
					hostedService.ScavengeSupportMessage));
		}

		var current = await ReadCurrentScavenge(cancellationToken);
		var last = await ReadLastScavenge(cancellationToken);
		var history = await ReadScavengeHistory(cancellationToken);

		return new AdminOperationsPage(
			subsystems,
			new ScavengeOperationsPanel(
				hostedService.SupportsScavenge,
				hostedService.ScavengeSupportMessage,
				current,
				last,
				history.Items,
				history.Message));
	}

	public async Task<ScavengeDetailPage> ReadScavengeDetail(
		string scavengeId,
		int page,
		long? fromEventNumber,
		CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(scavengeId))
			return ScavengeDetailPage.Unavailable("", "Missing scavenge id.");

		if (!await HasAccess(ScavengeReadOperation, cancellationToken))
			return ScavengeDetailPage.Unavailable(scavengeId, "Scavenge history access was denied.");

		var normalizedScavengeId = scavengeId.Trim();
		var normalizedPage = Math.Max(0, page);
		var streamId = $"{SystemStreams.ScavengesStream}-{normalizedScavengeId}";
		var read = await ReadStreamBackward(
			streamId,
			normalizedPage == 0 ? -1 : fromEventNumber ?? -1,
			ScavengeDetailPageSize,
			cancellationToken);

		if (!read.IsAvailable)
			return ScavengeDetailPage.Unavailable(scavengeId, read.Message);

		var rows = read.Events
			.Select(ScavengeDetailRow.From)
			.Where(x => x is not null)
			.ToArray();
		var current = await ReadCurrentScavenge(cancellationToken);
		var canStop = current.IsInProgress &&
			current.ScavengeId.Equals(normalizedScavengeId, StringComparison.OrdinalIgnoreCase);

		return ScavengeDetailPage.Success(
			normalizedScavengeId,
			normalizedPage,
			rows,
			read.NextEventNumber,
			read.IsEndOfStream,
			canStop);
	}

	public async Task<AdminCommandResult> StartScavenge(
		ScavengeStartRequest request,
		CancellationToken cancellationToken = default) {
		if (!hostedService.SupportsScavenge)
			return AdminCommandResult.Failed(hostedService.ScavengeSupportMessage, StatusCodes.Status400BadRequest);

		if (!await HasAccess(ScavengeStartOperation, cancellationToken))
			return AdminCommandResult.Failed("Scavenge start access was denied.", StatusCodes.Status403Forbidden);

		var validation = request.Validate();
		if (!string.IsNullOrWhiteSpace(validation))
			return AdminCommandResult.Failed(validation, StatusCodes.Status400BadRequest);

		var envelope = new TaskCompletionEnvelope<ClientMessage.ScavengeDatabaseStartedResponse>(
			mapFailure: ScavengeFailureMessage);
		try {
			publisher.Publish(new ClientMessage.ScavengeDatabase(
				envelope,
				Guid.NewGuid(),
				CurrentUser,
				request.StartFromChunk,
				request.Threads,
				request.Threshold,
				request.ThrottlePercent,
				request.SyncOnly));

			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return AdminCommandResult.Succeeded($"Scavenge '{completed.ScavengeId}' started.", completed.ScavengeId);
		} catch (TimeoutException) {
			return AdminCommandResult.Failed("Timed out starting scavenge.", StatusCodes.Status504GatewayTimeout);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return AdminCommandResult.Failed($"Unable to start scavenge: {UiMessages.Friendly(ex)}");
		}
	}

	public async Task<AdminCommandResult> StopScavenge(
		ScavengeStopRequest request,
		CancellationToken cancellationToken = default) {
		if (!hostedService.SupportsScavenge)
			return AdminCommandResult.Failed(hostedService.ScavengeSupportMessage, StatusCodes.Status400BadRequest);

		if (string.IsNullOrWhiteSpace(request.ScavengeId))
			return AdminCommandResult.Failed("Missing scavenge id.", StatusCodes.Status400BadRequest);

		if (!await HasAccess(ScavengeStopOperation, cancellationToken))
			return AdminCommandResult.Failed("Scavenge stop access was denied.", StatusCodes.Status403Forbidden);

		var envelope = new TaskCompletionEnvelope<ClientMessage.ScavengeDatabaseStoppedResponse>(
			mapFailure: ScavengeFailureMessage);
		try {
			publisher.Publish(new ClientMessage.StopDatabaseScavenge(
				envelope,
				Guid.NewGuid(),
				CurrentUser,
				request.ScavengeId.Trim()));

			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return AdminCommandResult.Succeeded($"Scavenge '{completed.ScavengeId}' stop requested.", completed.ScavengeId);
		} catch (TimeoutException) {
			return AdminCommandResult.Failed("Timed out stopping scavenge.", StatusCodes.Status504GatewayTimeout);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return AdminCommandResult.Failed($"Unable to stop scavenge: {UiMessages.Friendly(ex)}");
		}
	}

	public async Task<AdminCommandResult> ReloadConfig(CancellationToken cancellationToken = default) {
		if (!await HasAccess(ReloadConfigOperation, cancellationToken))
			return AdminCommandResult.Failed("Reload configuration access was denied.", StatusCodes.Status403Forbidden);

		publisher.Publish(new ClientMessage.ReloadConfig());
		return AdminCommandResult.Succeeded("Configuration reload requested.");
	}

	public async Task<AdminCommandResult> MergeIndexes(CancellationToken cancellationToken = default) {
		if (!await HasAccess(MergeIndexesOperation, cancellationToken))
			return AdminCommandResult.Failed("Merge indexes access was denied.", StatusCodes.Status403Forbidden);

		var envelope = new TaskCompletionEnvelope<ClientMessage.MergeIndexesResponse>();
		try {
			publisher.Publish(new ClientMessage.MergeIndexes(envelope, Guid.NewGuid(), CurrentUser));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return completed.Result == ClientMessage.MergeIndexesResponse.MergeIndexesResult.Started
				? AdminCommandResult.Succeeded("Index merge started.")
				: AdminCommandResult.Failed($"Unable to merge indexes. Result: {completed.Result}.");
		} catch (TimeoutException) {
			return AdminCommandResult.Failed("Timed out requesting index merge.", StatusCodes.Status504GatewayTimeout);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return AdminCommandResult.Failed($"Unable to merge indexes: {UiMessages.Friendly(ex)}");
		}
	}

	public async Task<AdminCommandResult> ResignNode(CancellationToken cancellationToken = default) {
		if (!await HasAccess(ResignOperation, cancellationToken))
			return AdminCommandResult.Failed("Resign node access was denied.", StatusCodes.Status403Forbidden);

		publisher.Publish(new ClientMessage.ResignNode());
		return AdminCommandResult.Succeeded("Node resignation requested.");
	}

	public async Task<AdminCommandResult> SetNodePriority(
		SetNodePriorityRequest request,
		CancellationToken cancellationToken = default) {
		if (!await HasAccess(SetPriorityOperation, cancellationToken))
			return AdminCommandResult.Failed("Set priority access was denied.", StatusCodes.Status403Forbidden);

		publisher.Publish(new ClientMessage.SetNodePriority(request.Priority));
		return AdminCommandResult.Succeeded($"Node priority set to {request.Priority}.");
	}

	public async Task<AdminCommandResult> Shutdown(CancellationToken cancellationToken = default) {
		if (!await HasAccess(ShutdownOperation, cancellationToken))
			return AdminCommandResult.Failed("Shutdown access was denied.", StatusCodes.Status403Forbidden);

		publisher.Publish(new ClientMessage.RequestShutdown(exitProcess: true, shutdownHttp: true));
		return AdminCommandResult.Succeeded("Node shutdown requested.");
	}

	private async Task<SubsystemsPanel> ReadSubsystems(CancellationToken cancellationToken) {
		if (!await HasAccess(SubsystemsOperation, cancellationToken))
			return SubsystemsPanel.Unavailable("Subsystem access was denied.");

		var subsystems = hostedService.EnabledNodeSubsystems
			.Select(x => new SubsystemView(x.ToString(), "Enabled"))
			.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return SubsystemsPanel.Success(subsystems);
	}

	private async Task<ScavengeStatusView> ReadCurrentScavenge(CancellationToken cancellationToken) {
		if (!await HasAccess(ScavengeReadOperation, cancellationToken))
			return ScavengeStatusView.Unavailable("Scavenge status access was denied.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.ScavengeDatabaseGetCurrentResponse>();

		try {
			publisher.Publish(new ClientMessage.GetCurrentDatabaseScavenge(envelope, Guid.NewGuid(), CurrentUser));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return completed.Result == ClientMessage.ScavengeDatabaseGetCurrentResponse.ScavengeResult.InProgress
				? ScavengeStatusView.InProgress(completed.ScavengeId)
				: ScavengeStatusView.Stopped();
		} catch (TimeoutException) {
			return ScavengeStatusView.Unavailable("Timed out reading current scavenge.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ScavengeStatusView.Unavailable($"Unable to read current scavenge: {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ScavengeStatusView> ReadLastScavenge(CancellationToken cancellationToken) {
		if (!await HasAccess(ScavengeReadOperation, cancellationToken))
			return ScavengeStatusView.Unavailable("Scavenge status access was denied.");

		var envelope = new TaskCompletionEnvelope<ClientMessage.ScavengeDatabaseGetLastResponse>();

		try {
			publisher.Publish(new ClientMessage.GetLastDatabaseScavenge(envelope, Guid.NewGuid(), CurrentUser));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return string.IsNullOrWhiteSpace(completed.ScavengeId)
				? ScavengeStatusView.Unknown(completed.Result.ToString())
				: new ScavengeStatusView(completed.ScavengeId, completed.Result.ToString(), "", completed.Result.ToString());
		} catch (TimeoutException) {
			return ScavengeStatusView.Unavailable("Timed out reading last scavenge.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ScavengeStatusView.Unavailable($"Unable to read last scavenge: {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ScavengeHistoryRead> ReadScavengeHistory(CancellationToken cancellationToken) {
		if (!await HasAccess(ScavengeReadOperation, cancellationToken))
			return ScavengeHistoryRead.Unavailable("Scavenge history access was denied.");

		var read = await ReadStreamBackward(
			SystemStreams.ScavengesStream,
			-1,
			ScavengeHistoryCount,
			cancellationToken);
		if (!read.IsAvailable)
			return ScavengeHistoryRead.Unavailable(read.Message);

		var byId = new Dictionary<string, ScavengeHistoryBuilder>(StringComparer.OrdinalIgnoreCase);
		foreach (var ev in read.Events) {
			var parsed = ParsedScavengeEvent.From(ev);
			if (parsed is null || string.IsNullOrWhiteSpace(parsed.ScavengeId))
				continue;

			if (!byId.TryGetValue(parsed.ScavengeId, out var builder)) {
				builder = new ScavengeHistoryBuilder(parsed.ScavengeId);
				byId.Add(parsed.ScavengeId, builder);
			}

			builder.Apply(parsed);
		}

		return ScavengeHistoryRead.Success(
			byId.Values
				.Select(x => x.Build())
				.Where(x => x.StartedUtc is not null)
				.OrderByDescending(x => x.StartedUtc)
				.ToArray());
	}

	private async Task<StreamReadResult> ReadStreamBackward(
		string streamId,
		long fromEventNumber,
		int count,
		CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<ClientMessage.ReadStreamEventsBackwardCompleted>();

		ClientMessage.ReadStreamEventsBackwardCompleted completed;
		try {
			publisher.Publish(new ClientMessage.ReadStreamEventsBackward(
				Guid.NewGuid(),
				Guid.NewGuid(),
				envelope,
				streamId,
				Math.Max(fromEventNumber, -1),
				count,
				resolveLinkTos: true,
				requireLeader: false,
				validationStreamVersion: null,
				CurrentUser,
				cancellationToken: cancellationToken));

			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return StreamReadResult.Unavailable($"Timed out reading '{streamId}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return StreamReadResult.Unavailable($"Unable to read '{streamId}': {UiMessages.Friendly(ex)}");
		}

		return completed.Result switch {
			ReadStreamResult.Success => StreamReadResult.Success(
				completed.Events.Select(ScavengeRawEvent.From).Where(x => x is not null).ToArray(),
				completed.NextEventNumber,
				completed.IsEndOfStream),
			ReadStreamResult.NoStream => StreamReadResult.Success(Array.Empty<ScavengeRawEvent>(), -1, true),
			ReadStreamResult.AccessDenied => StreamReadResult.Unavailable($"Read access was denied for '{streamId}'."),
			ReadStreamResult.StreamDeleted => StreamReadResult.Unavailable($"Stream '{streamId}' has been deleted."),
			_ => StreamReadResult.Unavailable(string.IsNullOrWhiteSpace(completed.Error)
				? $"Unable to read '{streamId}'. Result: {completed.Result}."
				: completed.Error)
		};
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static string ScavengeFailureMessage(Message message) =>
		message switch {
			ClientMessage.ScavengeDatabaseInProgressResponse inProgress => FriendlyMessage(inProgress.Reason),
			ClientMessage.ScavengeDatabaseNotFoundResponse notFound => FriendlyMessage(notFound.Reason),
			ClientMessage.ScavengeDatabaseUnauthorizedResponse unauthorized => FriendlyMessage(unauthorized.Reason),
			_ => null
		};

	private static string FriendlyMessage(string message) =>
		string.IsNullOrWhiteSpace(message) ? "The operation failed without a reason." : message;
}

public sealed record AdminOperationsPage(
	SubsystemsPanel Subsystems,
	ScavengeOperationsPanel Scavenge);

public sealed record SubsystemsPanel(
	IReadOnlyList<SubsystemView> Subsystems,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasSubsystems => Subsystems.Count > 0;

	public static SubsystemsPanel Success(IReadOnlyList<SubsystemView> subsystems) => new(subsystems, "");
	public static SubsystemsPanel Unavailable(string message) => new(Array.Empty<SubsystemView>(), message);
}

public sealed record SubsystemView(string Name, string Status);

public sealed record ScavengeOperationsPanel(
	bool SupportsScavenge,
	string SupportMessage,
	ScavengeStatusView Current,
	ScavengeStatusView Last,
	IReadOnlyList<ScavengeHistoryItem> History,
	string HistoryMessage) {
	public bool HasHistory => History.Count > 0;
}

public sealed record ScavengeStatusView(
	string ScavengeId,
	string Status,
	string Message,
	string Result) {
	public bool HasScavenge => !string.IsNullOrWhiteSpace(ScavengeId);
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool IsInProgress => Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase);
	public string DetailHref => HasScavenge ? $"/ui/operations/scavenges/{Uri.EscapeDataString(ScavengeId)}" : "";
	public string Tone => !IsAvailable
		? "bad"
		: IsInProgress
			? "warn"
			: Result.Contains("error", StringComparison.OrdinalIgnoreCase) || Result.Contains("failed", StringComparison.OrdinalIgnoreCase)
				? "bad"
				: Result.Contains("success", StringComparison.OrdinalIgnoreCase)
					? "good"
					: "muted";

	public static ScavengeStatusView InProgress(string scavengeId) => new(scavengeId ?? "", "InProgress", "", "InProgress");
	public static ScavengeStatusView Stopped() => new("", "Stopped", "", "Stopped");
	public static ScavengeStatusView Unknown(string result) => new("", "Unknown", "", result ?? "Unknown");
	public static ScavengeStatusView Unavailable(string message) => new("", "Unavailable", message, "Unavailable");
}

public sealed record ScavengeHistoryItem(
	string ScavengeId,
	string NodeEndpoint,
	DateTime? StartedUtc,
	DateTime? CompletedUtc,
	string Result,
	string Error) {
	public bool IsCompleted => CompletedUtc is not null;
	public bool CanStop => !IsCompleted;
	public string StartedLabel => StartedUtc?.ToUniversalTime().ToString("u") ?? "-";
	public string CompletedLabel => CompletedUtc?.ToUniversalTime().ToString("u") ?? "-";
	public string ResultLabel => string.IsNullOrWhiteSpace(Result) ? "Running" : Result;
	public string DetailHref => $"/ui/operations/scavenges/{Uri.EscapeDataString(ScavengeId)}";
	public string Tone => ResultLabel.Contains("error", StringComparison.OrdinalIgnoreCase) ||
	                     ResultLabel.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
	                     ResultLabel.Contains("interrupted", StringComparison.OrdinalIgnoreCase)
		? "bad"
		: IsCompleted
			? "good"
			: "warn";
}

public sealed record ScavengeDetailPage(
	string ScavengeId,
	int Page,
	IReadOnlyList<ScavengeDetailRow> Rows,
	long NextEventNumber,
	bool IsEndOfStream,
	bool CanStop,
	string Message) {
	private const int PageSize = 20;

	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasRows => Rows.Count > 0;
	public bool CanReadOlder => HasRows && !IsEndOfStream && NextEventNumber >= 0;
	public bool CanReadNewer => Page > 0;
	public long NewerFrom => HasRows ? Rows[0].EventNumber + PageSize : -1;
	public string OlderHref => $"/ui/operations/scavenges/{Uri.EscapeDataString(ScavengeId)}?page={Page + 1}&from={NextEventNumber}";
	public string NewerHref => Page <= 1
		? $"/ui/operations/scavenges/{Uri.EscapeDataString(ScavengeId)}"
		: $"/ui/operations/scavenges/{Uri.EscapeDataString(ScavengeId)}?page={Page - 1}&from={NewerFrom}";
	public string LatestHref => $"/ui/operations/scavenges/{Uri.EscapeDataString(ScavengeId)}";
	public string StreamHref => $"/ui/streams/{Uri.EscapeDataString(SystemStreams.ScavengesStream + "-" + ScavengeId)}";

	public static ScavengeDetailPage Success(
		string scavengeId,
		int page,
		IReadOnlyList<ScavengeDetailRow> rows,
		long nextEventNumber,
		bool isEndOfStream,
		bool canStop) =>
		new(scavengeId, page, rows, nextEventNumber, isEndOfStream, canStop, "");

	public static ScavengeDetailPage Unavailable(string scavengeId, string message) =>
		new(scavengeId, 0, Array.Empty<ScavengeDetailRow>(), -1, true, false, message);
}

public sealed record ScavengeDetailRow(
	long EventNumber,
	DateTime TimeStamp,
	string EventType,
	string Status,
	string SpaceSaved,
	string TimeTaken,
	string Result,
	string NodeEndpoint) {
	public string TimeStampLabel => TimeStamp.ToUniversalTime().ToString("u");
	public string Tone => Result.Contains("error", StringComparison.OrdinalIgnoreCase) ||
	                     Result.Contains("failed", StringComparison.OrdinalIgnoreCase)
		? "bad"
		: Result.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
		  Result.Contains("success", StringComparison.OrdinalIgnoreCase) ||
		  EventType.Equals(SystemEventTypes.ScavengeCompleted, StringComparison.OrdinalIgnoreCase)
			? "good"
			: "muted";

	internal static ScavengeDetailRow From(ScavengeRawEvent raw) {
		var parsed = ParsedScavengeEvent.From(raw);
		if (parsed is null)
			return new ScavengeDetailRow(raw.EventNumber, raw.TimeStamp, raw.EventType, raw.EventType, "-", "-", "", "");

		return raw.EventType switch {
			SystemEventTypes.ScavengeStarted => new ScavengeDetailRow(
				raw.EventNumber,
				raw.TimeStamp,
				raw.EventType,
				"Started",
				"-",
				"-",
				BuildStartedResult(parsed),
				parsed.NodeEndpoint),
			SystemEventTypes.ScavengeChunksCompleted => new ScavengeDetailRow(
				raw.EventNumber,
				raw.TimeStamp,
				raw.EventType,
				$"Scavenging chunks {parsed.ChunkStartNumber} - {parsed.ChunkEndNumber} complete",
				parsed.SpaceSavedLabel,
				parsed.TimeTaken,
				BuildChunkResult(parsed),
				parsed.NodeEndpoint),
			SystemEventTypes.ScavengeMergeCompleted => new ScavengeDetailRow(
				raw.EventNumber,
				raw.TimeStamp,
				raw.EventType,
				$"Merging chunks {parsed.ChunkStartNumber} - {parsed.ChunkEndNumber} complete",
				parsed.SpaceSavedLabel,
				parsed.TimeTaken,
				BuildMergeResult(parsed),
				parsed.NodeEndpoint),
			SystemEventTypes.ScavengeIndexCompleted => new ScavengeDetailRow(
				raw.EventNumber,
				raw.TimeStamp,
				raw.EventType,
				$"Scavenging index table ({parsed.Level},{parsed.Index}) complete",
				parsed.SpaceSavedLabel,
				parsed.TimeTaken,
				BuildIndexResult(parsed),
				parsed.NodeEndpoint),
			SystemEventTypes.ScavengeCompleted => new ScavengeDetailRow(
				raw.EventNumber,
				raw.TimeStamp,
				raw.EventType,
				"Completed",
				parsed.SpaceSavedLabel,
				parsed.TimeTaken,
				BuildCompletedResult(parsed),
				parsed.NodeEndpoint),
			_ => new ScavengeDetailRow(
				raw.EventNumber,
				raw.TimeStamp,
				raw.EventType,
				raw.EventType,
				parsed.SpaceSavedLabel,
				parsed.TimeTaken,
				FriendlyDetail(parsed.ErrorMessage, parsed.Result),
				parsed.NodeEndpoint)
		};
	}

	private static string BuildStartedResult(ParsedScavengeEvent parsed) {
		var parts = new List<string>();
		if (parsed.StartFromChunk.HasValue)
			parts.Add($"from chunk {parsed.StartFromChunk.Value}");
		if (parsed.Threads.HasValue)
			parts.Add($"{parsed.Threads.Value} thread(s)");
		return parts.Count == 0 ? "Started" : string.Join(", ", parts);
	}

	private static string BuildChunkResult(ParsedScavengeEvent parsed) {
		if (!string.IsNullOrWhiteSpace(parsed.ErrorMessage))
			return $"Error: {parsed.ErrorMessage}";

		return parsed.WasScavenged == true
			? $"{parsed.ChunkCountLabel} chunk(s) scavenged"
			: "No chunks scavenged";
	}

	private static string BuildMergeResult(ParsedScavengeEvent parsed) {
		if (!string.IsNullOrWhiteSpace(parsed.ErrorMessage))
			return $"Error: {parsed.ErrorMessage}";

		return parsed.WasMerged == true
			? $"{parsed.ChunkCountLabel} chunk(s) merged"
			: "No chunks merged";
	}

	private static string BuildIndexResult(ParsedScavengeEvent parsed) {
		if (!string.IsNullOrWhiteSpace(parsed.ErrorMessage))
			return $"Error: {parsed.ErrorMessage}";

		return parsed.WasScavenged == true
			? $"{parsed.EntriesDeleted.GetValueOrDefault()} index entries scavenged"
			: "Index table not scavenged";
	}

	private static string BuildCompletedResult(ParsedScavengeEvent parsed) =>
		parsed.Result.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parsed.Error)
			? $"{parsed.Result}: {parsed.Error}"
			: FriendlyDetail(parsed.Result, "Completed");

	private static string FriendlyDetail(string value, string fallback) =>
		string.IsNullOrWhiteSpace(value) ? fallback : value;
}

public sealed record AdminCommandResult(
	bool Success,
	string Message,
	string ScavengeId,
	int StatusCode) {
	public static AdminCommandResult Succeeded(string message, string scavengeId = "") =>
		new(true, message, scavengeId ?? "", StatusCodes.Status200OK);

	public static AdminCommandResult Failed(string message, int statusCode = StatusCodes.Status500InternalServerError) =>
		new(false, message, "", statusCode);
}

public sealed record ScavengeStartRequest(
	int StartFromChunk = 0,
	int Threads = 1,
	int? Threshold = null,
	int? ThrottlePercent = null,
	bool SyncOnly = false) {
	public string Validate() {
		if (StartFromChunk < 0)
			return "startFromChunk must be a positive integer.";
		if (Threads < 1)
			return "threads must be 1 or above.";
		if (ThrottlePercent is <= 0 or > 100)
			return "throttlePercent must be between 1 and 100 inclusive.";
		if (ThrottlePercent is not null && ThrottlePercent != 100 && Threads > 1)
			return "throttlePercent must be 100 for a multi-threaded scavenge.";
		return "";
	}
}

public sealed record ScavengeStopRequest(string ScavengeId);

public sealed record SetNodePriorityRequest(int Priority);

internal sealed record ScavengeHistoryRead(
	IReadOnlyList<ScavengeHistoryItem> Items,
	string Message) {
	public static ScavengeHistoryRead Success(IReadOnlyList<ScavengeHistoryItem> items) => new(items, "");
	public static ScavengeHistoryRead Unavailable(string message) => new(Array.Empty<ScavengeHistoryItem>(), message);
}

internal sealed record StreamReadResult(
	IReadOnlyList<ScavengeRawEvent> Events,
	long NextEventNumber,
	bool IsEndOfStream,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public static StreamReadResult Success(IReadOnlyList<ScavengeRawEvent> events, long nextEventNumber, bool isEndOfStream) =>
		new(events, nextEventNumber, isEndOfStream, "");
	public static StreamReadResult Unavailable(string message) => new(Array.Empty<ScavengeRawEvent>(), -1, true, message);
}

internal sealed record ScavengeRawEvent(
	string StreamId,
	long EventNumber,
	string EventType,
	Guid EventId,
	DateTime TimeStamp,
	string Data,
	string Metadata,
	bool IsJson) {
	public static ScavengeRawEvent From(ResolvedEvent resolvedEvent) {
		var record = resolvedEvent.Event ?? resolvedEvent.Link;
		return record is null
			? null
			: new ScavengeRawEvent(
				record.EventStreamId,
				record.EventNumber,
				record.EventType,
				record.EventId,
				record.TimeStamp,
				Decode(record.Data),
				Decode(record.Metadata),
				record.IsJson);
	}

	private static string Decode(ReadOnlyMemory<byte> data) =>
		data.IsEmpty ? "" : Encoding.UTF8.GetString(data.Span);
}

internal sealed class ScavengeHistoryBuilder(string scavengeId) {
	private string _nodeEndpoint = "";
	private DateTime? _startedUtc;
	private DateTime? _completedUtc;
	private string _result = "";
	private string _error = "";

	public void Apply(ParsedScavengeEvent parsed) {
		if (!string.IsNullOrWhiteSpace(parsed.NodeEndpoint))
			_nodeEndpoint = parsed.NodeEndpoint;

		switch (parsed.EventType) {
			case SystemEventTypes.ScavengeStarted:
				_startedUtc ??= parsed.TimeStamp;
				break;
			case SystemEventTypes.ScavengeCompleted:
				if (_completedUtc is null) {
					_completedUtc = parsed.TimeStamp;
					_result = string.IsNullOrWhiteSpace(parsed.Result) ? "Completed" : parsed.Result;
					_error = parsed.Error;
				}
				break;
		}
	}

	public ScavengeHistoryItem Build() =>
		new(scavengeId, _nodeEndpoint, _startedUtc, _completedUtc, _result, _error);
}

internal sealed record ParsedScavengeEvent(
	string ScavengeId,
	string EventType,
	DateTime TimeStamp,
	string NodeEndpoint,
	string Result,
	string Error,
	string ErrorMessage,
	string TimeTaken,
	long? SpaceSaved,
	int? ChunkStartNumber,
	int? ChunkEndNumber,
	bool? WasScavenged,
	bool? WasMerged,
	int? Level,
	int? Index,
	long? EntriesDeleted,
	int? StartFromChunk,
	int? Threads) {
	public string SpaceSavedLabel => SpaceSaved?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
	public int ChunkCount => ChunkStartNumber.HasValue && ChunkEndNumber.HasValue
		? Math.Max(0, ChunkEndNumber.Value - ChunkStartNumber.Value + 1)
		: 0;
	public string ChunkCountLabel => ChunkCount == 0 ? "0" : ChunkCount.ToString();

	public static ParsedScavengeEvent From(ScavengeRawEvent raw) {
		if (string.IsNullOrWhiteSpace(raw.Data))
			return null;

		try {
			using var document = JsonDocument.Parse(raw.Data);
			var root = document.RootElement;
			var scavengeId = ReadString(root, "scavengeId");
			if (string.IsNullOrWhiteSpace(scavengeId))
				scavengeId = IdFromStream(raw.StreamId);

			return new ParsedScavengeEvent(
				scavengeId,
				raw.EventType,
				raw.TimeStamp,
				ReadString(root, "nodeEndpoint"),
				ReadString(root, "result"),
				ReadString(root, "error"),
				ReadString(root, "errorMessage"),
				ReadString(root, "timeTaken"),
				ReadLong(root, "spaceSaved"),
				ReadInt(root, "chunkStartNumber"),
				ReadInt(root, "chunkEndNumber"),
				ReadBool(root, "wasScavenged"),
				ReadBool(root, "wasMerged"),
				ReadInt(root, "level"),
				ReadInt(root, "index"),
				ReadLong(root, "entriesDeleted"),
				ReadInt(root, "startFromChunk"),
				ReadInt(root, "threads"));
		} catch (JsonException) {
			return null;
		}
	}

	private static string IdFromStream(string streamId) =>
		streamId.StartsWith(SystemStreams.ScavengesStream + "-", StringComparison.Ordinal)
			? streamId[(SystemStreams.ScavengesStream.Length + 1)..]
			: "";

	private static string ReadString(JsonElement root, string name) {
		if (!root.TryGetProperty(name, out var value))
			return "";

		return value.ValueKind switch {
			JsonValueKind.String => value.GetString() ?? "",
			JsonValueKind.Number => value.ToString(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => ""
		};
	}

	private static int? ReadInt(JsonElement root, string name) {
		if (!root.TryGetProperty(name, out var value))
			return null;

		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
			return number;

		return int.TryParse(ReadString(root, name), out var parsed) ? parsed : null;
	}

	private static long? ReadLong(JsonElement root, string name) {
		if (!root.TryGetProperty(name, out var value))
			return null;

		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
			return number;

		return long.TryParse(ReadString(root, name), out var parsed) ? parsed : null;
	}

	private static bool? ReadBool(JsonElement root, string name) {
		if (!root.TryGetProperty(name, out var value))
			return null;

		return value.ValueKind switch {
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
			_ => null
		};
	}
}
