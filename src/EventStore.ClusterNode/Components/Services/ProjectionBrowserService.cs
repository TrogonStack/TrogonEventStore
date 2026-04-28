using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Plugins.Authorization;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services;
using Microsoft.AspNetCore.Http;

namespace EventStore.ClusterNode.Components.Services;

public sealed class ProjectionBrowserService(
	IPublisher publisher,
	IAuthorizationProvider authorizationProvider,
	IHttpContextAccessor httpContextAccessor) {
	private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
	private static readonly Operation ListOperation = new(Operations.Projections.List);
	private static readonly Operation StatisticsOperation = new(Operations.Projections.Statistics);
	private static readonly Operation ReadOperation = new(Operations.Projections.Read);
	private static readonly Operation StateOperation = new(Operations.Projections.State);
	private static readonly Operation ResultOperation = new(Operations.Projections.Result);
	private static readonly Operation ReadConfigurationOperation = new(Operations.Projections.ReadConfiguration);
	private static readonly Operation UpdateConfigurationOperation = new(Operations.Projections.UpdateConfiguration);
	private static readonly Operation UpdateOperation = new(Operations.Projections.Update);
	private static readonly Operation EnableOperation = new(Operations.Projections.Enable);
	private static readonly Operation DisableOperation = new(Operations.Projections.Disable);
	private static readonly Operation ResetOperation = new(Operations.Projections.Reset);
	private static readonly Operation DeleteOperation = new(Operations.Projections.Delete);
	private static readonly Operation CreateOneTimeOperation = new Operation(Operations.Projections.Create)
		.WithParameter(Operations.Projections.Parameters.OneTime);
	private static readonly Operation CreateContinuousOperation = new Operation(Operations.Projections.Create)
		.WithParameter(Operations.Projections.Parameters.Continuous);

	public static IReadOnlyList<StandardProjectionOption> StandardProjectionOptions { get; } = new[] {
		new StandardProjectionOption(
			"native:EventStore.Projections.Core.Standard.IndexStreams",
			"Index Streams"),
		new StandardProjectionOption(
			"native:EventStore.Projections.Core.Standard.CategorizeStreamByPath",
			"Categorize Stream by Path"),
		new StandardProjectionOption(
			"native:EventStore.Projections.Core.Standard.CategorizeEventsByStreamPath",
			"Categorize Event by Stream Path"),
		new StandardProjectionOption(
			"native:EventStore.Projections.Core.Standard.IndexEventsByEventType",
			"Index Events by Event Type"),
		new StandardProjectionOption(
			"native:EventStore.Projections.Core.Standard.StubHandler",
			"Reading Speed Test Handler")
	};

	public Task<ProjectionListPage> ReadAllNonTransient(CancellationToken cancellationToken = default) =>
		ReadAll(includeQueries: false, cancellationToken);

	public async Task<ProjectionListPage> ReadAll(bool includeQueries, CancellationToken cancellationToken = default) {
		if (!await HasAccess(ListOperation, cancellationToken))
			return ProjectionListPage.Unavailable("Projection list access was denied.");

		var read = await ReadStatistics(includeQueries ? null : ProjectionMode.AllNonTransient, name: null, cancellationToken);
		return read.IsAvailable
			? ProjectionListPage.Success(
				read.Projections.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
				includeQueries)
			: ProjectionListPage.Unavailable(read.Message, includeQueries);
	}

	public async Task<ProjectionDetailPage> ReadProjection(
		string name,
		string partition = "",
		CancellationToken cancellationToken = default) {
		name = NormalizeName(name);
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionDetailPage.Unavailable("", "Enter a projection name to inspect it.");

		if (!await HasAccess(StatisticsOperation, cancellationToken))
			return ProjectionDetailPage.Unavailable(name, "Projection statistics access was denied.");

		var read = await ReadStatistics(mode: null, name, cancellationToken);
		if (!read.IsAvailable)
			return ProjectionDetailPage.Unavailable(name, read.Message);

		var projection = read.Projections.FirstOrDefault();
		if (projection is null)
			return ProjectionDetailPage.Unavailable(name, $"Projection '{name}' was not found.");

		var query = await ReadQuery(name, cancellationToken);
		var state = await ReadState(name, partition, cancellationToken);
		var result = await ReadResult(name, partition, cancellationToken);

		return ProjectionDetailPage.Success(projection, query.IsAvailable ? query.Query : null, state, result, partition);
	}

	public async Task<ProjectionQueryPage> ReadProjectionQuery(string name, CancellationToken cancellationToken = default) {
		name = NormalizeName(name);
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionQueryPage.Unavailable(name ?? "", "Enter a projection name.");

		if (!await HasAccess(ReadOperation, cancellationToken))
			return ProjectionQueryPage.Unavailable(name, "Projection query access was denied.");

		var read = await ReadQuery(name, cancellationToken);
		return read.IsAvailable
			? ProjectionQueryPage.Success(read.Query)
			: ProjectionQueryPage.Unavailable(name, read.Message);
	}

	public async Task<ProjectionConfigPage> ReadProjectionConfig(string name, CancellationToken cancellationToken = default) {
		name = NormalizeName(name);
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionConfigPage.Unavailable(name ?? "", "Enter a projection name.");

		if (!await HasAccess(ReadConfigurationOperation, cancellationToken))
			return ProjectionConfigPage.Unavailable(name, "Projection configuration access was denied.");

		var read = await ReadConfig(name, cancellationToken);
		return read.IsAvailable
			? ProjectionConfigPage.Success(name, read.Config)
			: ProjectionConfigPage.Unavailable(name, read.Message);
	}

	public async Task<ProjectionCommandResult> Create(
		ProjectionCreateRequest request,
		CancellationToken cancellationToken = default) {
		var validation = ValidateCreate(request);
		if (!string.IsNullOrWhiteSpace(validation))
			return ProjectionCommandResult.Failure(request.Name, validation);

		var mode = request.Mode == ProjectionCreateMode.Continuous
			? ProjectionMode.Continuous
			: ProjectionMode.OneTime;
		var operation = request.Mode == ProjectionCreateMode.Continuous
			? CreateContinuousOperation
			: CreateOneTimeOperation;
		var checkpointsEnabled = request.Mode == ProjectionCreateMode.Continuous || request.CheckpointsEnabled;
		var emitEnabled = request.EmitEnabled;
		var trackEmittedStreams = emitEnabled && request.TrackEmittedStreams;
		var handlerType = NormalizeHandlerType(request.HandlerType);

		if (!await HasAccess(operation, cancellationToken))
			return ProjectionCommandResult.Failure(request.Name, "Projection creation access was denied.");

		return await RunUpdated(
			request.Name,
			"create projection",
			envelope => new ProjectionManagementMessage.Command.Post(
				envelope,
				mode,
				request.Name.Trim(),
				new ProjectionManagementMessage.RunAs(CurrentUser),
				handlerType,
				request.Query,
				request.Enabled,
				checkpointsEnabled,
				emitEnabled,
				trackEmittedStreams,
				enableRunAs: true),
			cancellationToken);
	}

	public async Task<ProjectionCommandResult> UpdateQuery(
		ProjectionUpdateQueryRequest request,
		CancellationToken cancellationToken = default) {
		var name = NormalizeName(request.Name);
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionCommandResult.Failure("", "Enter a projection name.");
		if (string.IsNullOrWhiteSpace(request.Query))
			return ProjectionCommandResult.Failure(name, "Enter projection source before saving.");

		if (!await HasAccess(UpdateOperation, cancellationToken))
			return ProjectionCommandResult.Failure(name, "Projection update access was denied.");

		return await RunUpdated(
			name,
			"update projection query",
			envelope => new ProjectionManagementMessage.Command.UpdateQuery(
				envelope,
				name,
				new ProjectionManagementMessage.RunAs(CurrentUser),
				request.Query,
				request.EmitEnabled),
			cancellationToken);
	}

	public async Task<ProjectionCommandResult> UpdateConfig(
		ProjectionConfigRequest request,
		CancellationToken cancellationToken = default) {
		var requestName = NormalizeName(request.Name);
		var normalizedRequest = request with { Name = requestName };
		var validation = ValidateConfig(normalizedRequest);
		if (!string.IsNullOrWhiteSpace(validation))
			return ProjectionCommandResult.Failure(requestName, validation);

		if (!await HasAccess(UpdateConfigurationOperation, cancellationToken))
			return ProjectionCommandResult.Failure(requestName, "Projection configuration update access was denied.");

		return await RunUpdated(
			requestName,
			"update projection configuration",
			envelope => new ProjectionManagementMessage.Command.UpdateConfig(
				envelope,
				requestName,
				normalizedRequest.EmitEnabled,
				normalizedRequest.TrackEmittedStreams,
				normalizedRequest.CheckpointAfterMs,
				normalizedRequest.CheckpointHandledThreshold,
				normalizedRequest.CheckpointUnhandledBytesThreshold,
				normalizedRequest.PendingEventsThreshold,
				normalizedRequest.MaxWriteBatchLength,
				normalizedRequest.MaxAllowedWritesInFlight,
				new ProjectionManagementMessage.RunAs(CurrentUser),
				normalizedRequest.ProjectionExecutionTimeout),
			cancellationToken);
	}

	public Task<ProjectionCommandResult> Enable(string name, CancellationToken cancellationToken = default) =>
		RunNamedCommand(
			name,
			EnableOperation,
			"enable projection",
			(normalizedName, envelope) => new ProjectionManagementMessage.Command.Enable(
				envelope,
				normalizedName,
				new ProjectionManagementMessage.RunAs(CurrentUser)),
			cancellationToken);

	public Task<ProjectionCommandResult> Disable(string name, CancellationToken cancellationToken = default) =>
		RunNamedCommand(
			name,
			DisableOperation,
			"disable projection",
			(normalizedName, envelope) => new ProjectionManagementMessage.Command.Disable(
				envelope,
				normalizedName,
				new ProjectionManagementMessage.RunAs(CurrentUser)),
			cancellationToken);

	public Task<ProjectionCommandResult> Reset(string name, CancellationToken cancellationToken = default) =>
		RunNamedCommand(
			name,
			ResetOperation,
			"reset projection",
			(normalizedName, envelope) => new ProjectionManagementMessage.Command.Reset(
				envelope,
				normalizedName,
				new ProjectionManagementMessage.RunAs(CurrentUser)),
			cancellationToken);

	public async Task<ProjectionBulkCommandResult> EnableAll(bool includeQueries, CancellationToken cancellationToken = default) {
		var page = await ReadAll(includeQueries, cancellationToken);
		if (!page.IsAvailable)
			return ProjectionBulkCommandResult.Failure(page.Message);

		var candidates = page.Projections.Where(x => !x.IsRunning).ToArray();
		return await RunBulk(candidates, projection => Enable(projection.Name, cancellationToken), "enable");
	}

	public async Task<ProjectionBulkCommandResult> DisableAll(bool includeQueries, CancellationToken cancellationToken = default) {
		var page = await ReadAll(includeQueries, cancellationToken);
		if (!page.IsAvailable)
			return ProjectionBulkCommandResult.Failure(page.Message);

		var candidates = page.Projections.Where(x => x.IsRunning).ToArray();
		return await RunBulk(candidates, projection => Disable(projection.Name, cancellationToken), "disable");
	}

	public async Task<ProjectionCommandResult> Delete(
		ProjectionDeleteRequest request,
		CancellationToken cancellationToken = default) {
		var name = NormalizeName(request.Name);
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionCommandResult.Failure("", "Enter a projection name.");

		if (!await HasAccess(DeleteOperation, cancellationToken))
			return ProjectionCommandResult.Failure(name, "Projection delete access was denied.");

		return await RunUpdated(
			name,
			"delete projection",
			envelope => new ProjectionManagementMessage.Command.Delete(
				envelope,
				name,
				new ProjectionManagementMessage.RunAs(CurrentUser),
				request.DeleteCheckpointStream,
				request.DeleteStateStream,
				request.DeleteEmittedStreams),
			cancellationToken);
	}

	private async Task<ProjectionQueryRead> ReadQuery(string name, CancellationToken cancellationToken) {
		if (!await HasAccess(ReadOperation, cancellationToken))
			return ProjectionQueryRead.Unavailable("Projection query access was denied.");

		var envelope = ProjectionEnvelope<ProjectionManagementMessage.ProjectionQuery>();

		try {
			publisher.Publish(new ProjectionManagementMessage.Command.GetQuery(
				envelope,
				name,
				new ProjectionManagementMessage.RunAs(CurrentUser)));

			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return ProjectionQueryRead.Success(ProjectionQueryView.From(completed));
		} catch (TimeoutException) {
			return ProjectionQueryRead.Unavailable($"Timed out reading query for '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionQueryRead.Unavailable($"Unable to read query for '{name}': {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ProjectionConfigRead> ReadConfig(string name, CancellationToken cancellationToken) {
		var envelope = ProjectionEnvelope<ProjectionManagementMessage.ProjectionConfig>();

		try {
			publisher.Publish(new ProjectionManagementMessage.Command.GetConfig(
				envelope,
				name,
				new ProjectionManagementMessage.RunAs(CurrentUser)));

			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return ProjectionConfigRead.Success(ProjectionConfigView.From(completed));
		} catch (TimeoutException) {
			return ProjectionConfigRead.Unavailable($"Timed out reading configuration for '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionConfigRead.Unavailable($"Unable to read configuration for '{name}': {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ProjectionDataRead> ReadState(
		string name,
		string partition,
		CancellationToken cancellationToken) {
		if (!await HasAccess(StateOperation, cancellationToken))
			return ProjectionDataRead.Unavailable("Projection state access was denied.");

		var envelope = ProjectionEnvelope<ProjectionManagementMessage.ProjectionState>();

		try {
			publisher.Publish(new ProjectionManagementMessage.Command.GetState(envelope, name, partition ?? ""));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			if (completed.Exception is not null)
				return ProjectionDataRead.Unavailable(completed.Exception.Message);

			return ProjectionDataRead.Success(completed.State ?? "", completed.Position?.ToString() ?? "");
		} catch (TimeoutException) {
			return ProjectionDataRead.Unavailable($"Timed out reading state for '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionDataRead.Unavailable($"Unable to read state for '{name}': {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ProjectionDataRead> ReadResult(
		string name,
		string partition,
		CancellationToken cancellationToken) {
		if (!await HasAccess(ResultOperation, cancellationToken))
			return ProjectionDataRead.Unavailable("Projection result access was denied.");

		var envelope = ProjectionEnvelope<ProjectionManagementMessage.ProjectionResult>();

		try {
			publisher.Publish(new ProjectionManagementMessage.Command.GetResult(envelope, name, partition ?? ""));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			if (completed.Exception is not null)
				return ProjectionDataRead.Unavailable(completed.Exception.Message);

			return ProjectionDataRead.Success(completed.Result ?? "", completed.Position?.ToString() ?? "");
		} catch (TimeoutException) {
			return ProjectionDataRead.Unavailable($"Timed out reading result for '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionDataRead.Unavailable($"Unable to read result for '{name}': {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ProjectionCommandResult> RunNamedCommand(
		string name,
		Operation operation,
		string action,
		Func<string, IEnvelope, Message> createMessage,
		CancellationToken cancellationToken) {
		name = NormalizeName(name);
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionCommandResult.Failure("", "Enter a projection name.");

		if (!await HasAccess(operation, cancellationToken))
			return ProjectionCommandResult.Failure(name, $"Projection {action} access was denied.");

		return await RunUpdated(name, action, envelope => createMessage(name, envelope), cancellationToken);
	}

	private async Task<ProjectionCommandResult> RunUpdated(
		string name,
		string action,
		Func<IEnvelope, Message> createMessage,
		CancellationToken cancellationToken) {
		var envelope = ProjectionEnvelope<ProjectionManagementMessage.Updated>();

		try {
			publisher.Publish(createMessage(envelope));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			var projectionName = string.IsNullOrWhiteSpace(completed.Name) ? name : completed.Name;
			return ProjectionCommandResult.Succeeded(projectionName, $"Projection '{projectionName}' {ActionPastTense(action)}.");
		} catch (TimeoutException) {
			return ProjectionCommandResult.Failure(name, $"Timed out trying to {action} '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionCommandResult.Failure(name, $"Unable to {action} '{name}': {UiMessages.Friendly(ex)}");
		}
	}

	private async Task<ProjectionBulkCommandResult> RunBulk(
		IReadOnlyList<ProjectionView> candidates,
		Func<ProjectionView, Task<ProjectionCommandResult>> run,
		string action) {
		if (candidates.Count == 0)
			return ProjectionBulkCommandResult.Succeeded(0, $"No projections needed {action}.");

		var failures = new List<string>();
		var completed = 0;
		foreach (var projection in candidates) {
			var result = await run(projection);
			if (result.Success)
				completed++;
			else
				failures.Add($"{projection.Name}: {result.Message}");
		}

		return failures.Count == 0
			? ProjectionBulkCommandResult.Succeeded(completed, $"{completed} projection(s) queued for {action}.")
			: ProjectionBulkCommandResult.Partial(completed, failures);
	}

	private async Task<ProjectionStatisticsRead> ReadStatistics(
		ProjectionMode? mode,
		string name,
		CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<ProjectionManagementMessage.Statistics>(
			mapReply: message => message is ProjectionManagementMessage.NotFound
				? new ProjectionManagementMessage.Statistics(Array.Empty<ProjectionStatistics>())
				: null,
			mapFailure: ProjectionFailureMessage);

		try {
			publisher.Publish(new ProjectionManagementMessage.Command.GetStatistics(envelope, mode, name, includeDeleted: true));
			var completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
			return ProjectionStatisticsRead.Success(completed.Projections.Select(ProjectionView.From).ToArray());
		} catch (TimeoutException) {
			return ProjectionStatisticsRead.Unavailable(name is null
				? "Timed out reading projections."
				: $"Timed out reading projection '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionStatisticsRead.Unavailable(name is null
				? $"Unable to read projections: {UiMessages.Friendly(ex)}"
				: $"Unable to read projection '{name}': {UiMessages.Friendly(ex)}");
		}
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static TaskCompletionEnvelope<T> ProjectionEnvelope<T>() where T : Message =>
		new(mapFailure: ProjectionFailureMessage);

	private static string ProjectionFailureMessage(Message message) =>
		message is ProjectionManagementMessage.OperationFailed failed ? failed.Reason ?? "" : null;

	private static string NormalizeName(string name) {
		if (string.IsNullOrWhiteSpace(name))
			return name;

		var value = name.Trim();
		if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
			value = absolute.AbsolutePath;

		value = value.TrimStart('/');
		const string projectionPrefix = "projection/";
		if (value.StartsWith(projectionPrefix, StringComparison.OrdinalIgnoreCase))
			value = value[projectionPrefix.Length..];

		return value;
	}

	private static string ActionPastTense(string action) =>
		action switch {
			"create projection" => "created",
			"update projection query" => "query updated",
			"update projection configuration" => "configuration updated",
			"enable projection" => "enabled",
			"disable projection" => "disabled",
			"reset projection" => "reset",
			"delete projection" => "deleted",
			_ => "updated"
		};

	private static string ValidateCreate(ProjectionCreateRequest request) {
		if (string.IsNullOrWhiteSpace(request.Name))
			return "Enter a projection name.";
		if (string.IsNullOrWhiteSpace(request.Query))
			return "Enter projection source before creating it.";
		if (request.EmitEnabled && !request.CheckpointsEnabled && request.Mode != ProjectionCreateMode.Continuous)
			return "Emitting requires checkpoints.";
		if (!IsKnownHandlerType(request.HandlerType))
			return "Select a supported projection handler.";

		return "";
	}

	private static string NormalizeHandlerType(string handlerType) =>
		string.IsNullOrWhiteSpace(handlerType) ? "JS" : handlerType.Trim();

	private static bool IsKnownHandlerType(string handlerType) {
		var normalized = NormalizeHandlerType(handlerType);
		return string.Equals(normalized, "JS", StringComparison.Ordinal) ||
			StandardProjectionOptions.Any(option => string.Equals(option.HandlerType, normalized, StringComparison.Ordinal));
	}

	private static string ValidateConfig(ProjectionConfigRequest request) {
		if (string.IsNullOrWhiteSpace(request.Name))
			return "Enter a projection name.";
		if (request.ProjectionExecutionTimeout is <= 0)
			return "Projection execution timeout must be positive.";
		if (request.CheckpointAfterMs < 0 ||
			request.CheckpointHandledThreshold < 0 ||
			request.CheckpointUnhandledBytesThreshold < 0 ||
			request.PendingEventsThreshold < 0 ||
			request.MaxWriteBatchLength < 0 ||
			request.MaxAllowedWritesInFlight < 0)
			return "Projection configuration values cannot be negative.";

		return "";
	}
}

internal sealed record ProjectionStatisticsRead(
	IReadOnlyList<ProjectionView> Projections,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public static ProjectionStatisticsRead Success(IReadOnlyList<ProjectionView> projections) => new(projections, "");
	public static ProjectionStatisticsRead Unavailable(string message) => new(Array.Empty<ProjectionView>(), message);
}

internal sealed record ProjectionQueryRead(
	ProjectionQueryView Query,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public static ProjectionQueryRead Success(ProjectionQueryView query) => new(query, "");
	public static ProjectionQueryRead Unavailable(string message) => new(null, message);
}

internal sealed record ProjectionConfigRead(
	ProjectionConfigView Config,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public static ProjectionConfigRead Success(ProjectionConfigView config) => new(config, "");
	public static ProjectionConfigRead Unavailable(string message) => new(null, message);
}

public sealed record ProjectionDataRead(
	string Value,
	string Position,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasValue => IsAvailable && !string.IsNullOrWhiteSpace(Value);
	public static ProjectionDataRead Success(string value, string position) => new(value, position, "");
	public static ProjectionDataRead Unavailable(string message) =>
		new("", "", string.IsNullOrWhiteSpace(message) ? "Projection data is unavailable." : message);
}

public sealed record ProjectionListPage(
	IReadOnlyList<ProjectionView> Projections,
	bool IncludeQueries,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public bool HasProjections => Projections.Count > 0;
	public int RunningCount => Projections.Count(x => x.IsRunning);
	public int FaultedCount => Projections.Count(x => x.IsFaulted);
	public int DisabledCount => Projections.Count(x => !x.Enabled);
	public string RunningCountLabel => IsAvailable ? RunningCount.ToString() : "-";
	public string FaultedCountLabel => IsAvailable ? FaultedCount.ToString() : "-";
	public string DisabledCountLabel => IsAvailable ? DisabledCount.ToString() : "-";
	public string ToggleQueriesHref => IncludeQueries ? "/ui/projections" : "/ui/projections?includeQueries=true";
	public string ToggleQueriesLabel => IncludeQueries ? "Hide transient queries" : "Include transient queries";

	public static ProjectionListPage Success(IReadOnlyList<ProjectionView> projections, bool includeQueries) =>
		new(projections, includeQueries, "");

	public static ProjectionListPage Unavailable(string message, bool includeQueries = false) =>
		new(Array.Empty<ProjectionView>(), includeQueries, message);
}

public sealed record ProjectionDetailPage(
	ProjectionView Projection,
	ProjectionQueryView Query,
	ProjectionDataRead State,
	ProjectionDataRead Result,
	string Name,
	string Partition,
	string Message) {
	public bool HasProjection => Projection is not null;

	public static ProjectionDetailPage Success(
		ProjectionView projection,
		ProjectionQueryView query,
		ProjectionDataRead state,
		ProjectionDataRead result,
		string partition) =>
		new(projection, query, state, result, projection.Name, partition ?? "", "");

	public static ProjectionDetailPage Unavailable(string name, string message) =>
		new(null, null, ProjectionDataRead.Unavailable(message), ProjectionDataRead.Unavailable(message), name, "", message);
}

public sealed record ProjectionQueryPage(
	ProjectionQueryView Query,
	string Name,
	string Message) {
	public bool HasQuery => Query is not null;

	public static ProjectionQueryPage Success(ProjectionQueryView query) => new(query, query.Name, "");
	public static ProjectionQueryPage Unavailable(string name, string message) => new(null, name, message);
}

public sealed record ProjectionConfigPage(
	ProjectionConfigView Config,
	string Name,
	string Message) {
	public bool HasConfig => Config is not null;

	public static ProjectionConfigPage Success(string name, ProjectionConfigView config) => new(config, name, "");
	public static ProjectionConfigPage Unavailable(string name, string message) => new(null, name, message);
}

public sealed record ProjectionCommandResult(
	string Name,
	bool Success,
	string Message) {
	public static ProjectionCommandResult Succeeded(string name, string message) => new(name, true, message);
	public static ProjectionCommandResult Failure(string name, string message) => new(name, false, message);
}

public sealed record ProjectionBulkCommandResult(
	int Completed,
	bool Success,
	string Message,
	IReadOnlyList<string> Failures) {
	public static ProjectionBulkCommandResult Succeeded(int completed, string message) =>
		new(completed, true, message, Array.Empty<string>());

	public static ProjectionBulkCommandResult Failure(string message) =>
		new(0, false, message, Array.Empty<string>());

	public static ProjectionBulkCommandResult Partial(int completed, IReadOnlyList<string> failures) =>
		new(completed, false, $"{completed} projection(s) updated; {failures.Count} failed.", failures);
}

public enum ProjectionCreateMode {
	OneTime,
	Continuous
}

public sealed record ProjectionCreateRequest(
	ProjectionCreateMode Mode,
	string Name,
	string Query,
	bool Enabled,
	bool CheckpointsEnabled,
	bool EmitEnabled,
	bool TrackEmittedStreams,
	string HandlerType = "JS");

public sealed record ProjectionUpdateQueryRequest(
	string Name,
	string Query,
	bool? EmitEnabled);

public sealed record ProjectionConfigRequest(
	string Name,
	bool EmitEnabled,
	bool TrackEmittedStreams,
	int CheckpointAfterMs,
	int CheckpointHandledThreshold,
	int CheckpointUnhandledBytesThreshold,
	int PendingEventsThreshold,
	int MaxWriteBatchLength,
	int MaxAllowedWritesInFlight,
	int? ProjectionExecutionTimeout);

public sealed record ProjectionDeleteRequest(
	string Name,
	bool DeleteCheckpointStream,
	bool DeleteStateStream,
	bool DeleteEmittedStreams);

public sealed record StandardProjectionOption(
	string HandlerType,
	string Label);

public sealed record ProjectionQueryView(
	string Name,
	string Query,
	bool EmitEnabled,
	string Type,
	bool? TrackEmittedStreams,
	bool? CheckpointsEnabled) {
	public string EmitLabel => EmitEnabled ? "Emit enabled" : "Emit disabled";
	public string CheckpointsLabel => CheckpointsEnabled == true ? "Checkpoints enabled" : "Checkpoints disabled";
	public string TrackEmittedStreamsLabel => TrackEmittedStreams == true ? "Tracking emitted streams" : "Not tracking emitted streams";

	public static ProjectionQueryView From(ProjectionManagementMessage.ProjectionQuery source) =>
		new(
			source.Name ?? "",
			source.Query ?? "",
			source.EmitEnabled,
			source.Type ?? "",
			source.TrackEmittedStreams,
			source.CheckpointsEnabled);
}

public sealed record ProjectionConfigView(
	bool EmitEnabled,
	bool TrackEmittedStreams,
	int CheckpointAfterMs,
	int CheckpointHandledThreshold,
	int CheckpointUnhandledBytesThreshold,
	int PendingEventsThreshold,
	int MaxWriteBatchLength,
	int MaxAllowedWritesInFlight,
	int? ProjectionExecutionTimeout) {
	public static ProjectionConfigView From(ProjectionManagementMessage.ProjectionConfig source) =>
		new(
			source.EmitEnabled,
			source.TrackEmittedStreams,
			source.CheckpointAfterMs,
			source.CheckpointHandledThreshold,
			source.CheckpointUnhandledBytesThreshold,
			source.PendingEventsThreshold,
			source.MaxWriteBatchLength,
			source.MaxAllowedWritesInFlight,
			source.ProjectionExecutionTimeout);
}

public sealed record ProjectionView(
	string Name,
	string EffectiveName,
	string Status,
	string StateReason,
	bool Enabled,
	string Mode,
	string Position,
	float Progress,
	string LastCheckpoint,
	string CheckpointStatus,
	int EventsProcessedAfterRestart,
	int BufferedEvents,
	int ReadsInProgress,
	int WritesInProgress,
	int WritePendingEventsBeforeCheckpoint,
	int WritePendingEventsAfterCheckpoint,
	int PartitionsCached,
	long CoreProcessingTime,
	string ResultStreamName) {
	public bool IsRunning => Status.Contains("running", StringComparison.OrdinalIgnoreCase);
	public bool IsFaulted => Status.Contains("fault", StringComparison.OrdinalIgnoreCase);
	public bool IsTransient => string.Equals(Mode, ProjectionMode.Transient.ToString(), StringComparison.OrdinalIgnoreCase);
	public string StatusLabel => string.IsNullOrWhiteSpace(Status) ? "Unknown" : Status;
	public string StatusTone => IsFaulted
		? "bad"
		: IsRunning
			? "good"
			: Enabled
				? "warn"
				: "muted";
	public string EnabledLabel => Enabled ? "Enabled" : "Disabled";
	public string EnabledTone => Enabled ? "good" : "muted";
	public string ProgressLabel => $"{Progress:0.##}%";
	public string WriteQueuesLabel => $"{WritePendingEventsBeforeCheckpoint} / {WritePendingEventsAfterCheckpoint}";
	public string EventsPerSecondLabel => CoreProcessingTime <= 0
		? "0"
		: $"{EventsProcessedAfterRestart * 1000d / CoreProcessingTime:0.##}";
	public string DetailHref => $"/ui/projections/{Uri.EscapeDataString(Name)}";
	public string EditHref => $"/ui/projections/edit/{Uri.EscapeDataString(Name)}";
	public string ConfigHref => $"/ui/projections/config/{Uri.EscapeDataString(Name)}";
	public string DeleteHref => $"/ui/projections/delete/{Uri.EscapeDataString(Name)}";
	public string DebugHref => $"/ui/projections/debug/{Uri.EscapeDataString(Name)}";
	public string RawStatisticsHref => $"/projection/{Uri.EscapeDataString(Name)}/statistics";
	public string RawStateHref => $"/projection/{Uri.EscapeDataString(Name)}/state";
	public string RawResultHref => $"/projection/{Uri.EscapeDataString(Name)}/result";
	public string RawQueryHref => $"/projection/{Uri.EscapeDataString(Name)}/query?config=yes";
	public string RawConfigHref => $"/projection/{Uri.EscapeDataString(Name)}/config";
	public string ResultStreamHref => string.IsNullOrWhiteSpace(ResultStreamName)
		? ""
		: $"/ui/streams/{Uri.EscapeDataString(ResultStreamName)}";

	public static ProjectionView From(ProjectionStatistics source) =>
		new(
			source.Name ?? "",
			string.IsNullOrWhiteSpace(source.EffectiveName) ? source.Name ?? "" : source.EffectiveName,
			source.Status ?? "",
			source.StateReason ?? "",
			source.Enabled,
			source.Mode.ToString(),
			source.Position ?? "",
			source.Progress,
			source.LastCheckpoint ?? "",
			source.CheckpointStatus ?? "",
			source.EventsProcessedAfterRestart,
			source.BufferedEvents,
			source.ReadsInProgress,
			source.WritesInProgress,
			source.WritePendingEventsBeforeCheckpoint,
			source.WritePendingEventsAfterCheckpoint,
			source.PartitionsCached,
			source.CoreProcessingTime,
			source.ResultStreamName ?? "");
}
