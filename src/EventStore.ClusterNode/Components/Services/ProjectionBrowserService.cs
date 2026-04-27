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

	public async Task<ProjectionListPage> ReadAllNonTransient(CancellationToken cancellationToken = default) {
		if (!await HasAccess(ListOperation, cancellationToken))
			return ProjectionListPage.Unavailable("Projection list access was denied.");

		var read = await ReadStatistics(ProjectionMode.AllNonTransient, name: null, cancellationToken);
		return read.IsAvailable
			? ProjectionListPage.Success(read.Projections.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray())
			: ProjectionListPage.Unavailable(read.Message);
	}

	public async Task<ProjectionDetailPage> ReadProjection(string name, CancellationToken cancellationToken = default) {
		if (string.IsNullOrWhiteSpace(name))
			return ProjectionDetailPage.Unavailable("", "Enter a projection name to inspect it.");

		if (!await HasAccess(StatisticsOperation, cancellationToken))
			return ProjectionDetailPage.Unavailable(name, "Projection statistics access was denied.");

		var read = await ReadStatistics(mode: null, name, cancellationToken);
		if (!read.IsAvailable)
			return ProjectionDetailPage.Unavailable(name, read.Message);

		var projection = read.Projections.FirstOrDefault();
		return projection is null
			? ProjectionDetailPage.Unavailable(name, $"Projection '{name}' was not found.")
			: ProjectionDetailPage.Success(projection);
	}

	private async Task<ProjectionStatisticsRead> ReadStatistics(
		ProjectionMode? mode,
		string name,
		CancellationToken cancellationToken) {
		var envelope = new TaskCompletionEnvelope<ProjectionManagementMessage.Statistics>();
		publisher.Publish(new ProjectionManagementMessage.Command.GetStatistics(envelope, mode, name, includeDeleted: true));

		ProjectionManagementMessage.Statistics completed;
		try {
			completed = await envelope.Task.WaitAsync(ReadTimeout, cancellationToken);
		} catch (TimeoutException) {
			return ProjectionStatisticsRead.Unavailable(name is null
				? "Timed out reading projections."
				: $"Timed out reading projection '{name}'.");
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) {
			return ProjectionStatisticsRead.Unavailable(name is null
				? $"Unable to read projections: {FriendlyMessage(ex)}"
				: $"Unable to read projection '{name}': {FriendlyMessage(ex)}");
		}

		return ProjectionStatisticsRead.Success(completed.Projections.Select(ProjectionView.From).ToArray());
	}

	private ClaimsPrincipal CurrentUser =>
		httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal(new ClaimsIdentity());

	private Task<bool> HasAccess(Operation operation, CancellationToken cancellationToken) =>
		authorizationProvider.CheckAccessAsync(CurrentUser, operation, cancellationToken).AsTask();

	private static string FriendlyMessage(Exception ex) =>
		string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

	private sealed class TaskCompletionEnvelope<T> : IEnvelope where T : Message {
		private readonly TaskCompletionSource<T> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<T> Task => _source.Task;

		public void ReplyWith<U>(U message) where U : Message {
			if (message is T typed) {
				_source.TrySetResult(typed);
				return;
			}

			if (message is ProjectionManagementMessage.NotFound) {
				_source.TrySetException(new InvalidOperationException("Projection was not found."));
				return;
			}

			_source.TrySetException(new InvalidOperationException(
				$"Expected {typeof(T).Name} but received {message.GetType().Name}."));
		}
	}
}

internal sealed record ProjectionStatisticsRead(
	IReadOnlyList<ProjectionView> Projections,
	string Message) {
	public bool IsAvailable => string.IsNullOrWhiteSpace(Message);
	public static ProjectionStatisticsRead Success(IReadOnlyList<ProjectionView> projections) => new(projections, "");
	public static ProjectionStatisticsRead Unavailable(string message) => new(Array.Empty<ProjectionView>(), message);
}

public sealed record ProjectionListPage(
	IReadOnlyList<ProjectionView> Projections,
	string Message) {
	public bool HasProjections => Projections.Count > 0;
	public int RunningCount => Projections.Count(x => x.IsRunning);
	public int FaultedCount => Projections.Count(x => x.IsFaulted);
	public int DisabledCount => Projections.Count(x => !x.Enabled);

	public static ProjectionListPage Success(IReadOnlyList<ProjectionView> projections) => new(projections, "");
	public static ProjectionListPage Unavailable(string message) => new(Array.Empty<ProjectionView>(), message);
}

public sealed record ProjectionDetailPage(
	ProjectionView Projection,
	string Name,
	string Message) {
	public bool HasProjection => Projection is not null;

	public static ProjectionDetailPage Success(ProjectionView projection) => new(projection, projection.Name, "");
	public static ProjectionDetailPage Unavailable(string name, string message) => new(null, name, message);
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
	int PartitionsCached,
	long CoreProcessingTime,
	string ResultStreamName) {
	public bool IsRunning => Status.Contains("running", StringComparison.OrdinalIgnoreCase);
	public bool IsFaulted => Status.Contains("fault", StringComparison.OrdinalIgnoreCase);
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
	public string DetailHref => $"/ui/projections/{Uri.EscapeDataString(Name)}";
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
			source.PartitionsCached,
			source.CoreProcessingTime,
			source.ResultStreamName ?? "");
}
