using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Projections.Core.Metrics;

public class ProjectionTracker : IProjectionTracker
{
	private ProjectionStatistics[] _currentStats = [];

	public void OnNewStats(ProjectionStatistics[] newStats)
	{
		_currentStats = newStats ?? [];
	}

	public IEnumerable<Measurement<long>> ObserveEventsProcessed() =>
		_currentStats.Select(x =>
			new Measurement<long>(
				x.EventsProcessedAfterRestart,
				[
					new(TrogonAttributeNames.ProjectionName, x.Name)
				]));

	public IEnumerable<Measurement<float>> ObserveProgress() =>
		_currentStats.Select(x =>
			new Measurement<float>(
				NormalizeProgress(x.Progress),
				[
					new(TrogonAttributeNames.ProjectionName, x.Name)
				]));

	public IEnumerable<Measurement<long>> ObserveStatus()
	{
		foreach (var statistics in _currentStats)
		{
			var projectionRunning = 0;
			var projectionFaulted = 0;
			var projectionStopped = 0;

			if (statistics.LeaderStatus == ManagedProjectionState.Running)
			{
				projectionRunning = 1;
			}
			else if (statistics.LeaderStatus == ManagedProjectionState.Stopped)
			{
				projectionStopped = 1;
			}
			else if (statistics.LeaderStatus == ManagedProjectionState.Faulted)
			{
				projectionFaulted = 1;
			}

			yield return new(projectionRunning, [
				new(TrogonAttributeNames.ProjectionName, statistics.Name),
				new(TrogonAttributeNames.ProjectionStatus, "running"),
			]);

			yield return new(projectionFaulted, [
				new(TrogonAttributeNames.ProjectionName, statistics.Name),
				new(TrogonAttributeNames.ProjectionStatus, "faulted"),
			]);

			yield return new(projectionStopped, [
				new(TrogonAttributeNames.ProjectionName, statistics.Name),
				new(TrogonAttributeNames.ProjectionStatus, "stopped"),
			]);
		}
	}

	public IEnumerable<Measurement<long>> ObserveStateSize()
	{
		foreach (var statistics in _currentStats)
		{
			if (statistics.StateSizes is null)
			{
				continue;
			}

			yield return new(
				statistics.StateSizes.Values.Sum(stateSize => (long)stateSize),
				new KeyValuePair<string, object>(TrogonAttributeNames.ProjectionName, statistics.Name));
		}
	}

	private static float NormalizeProgress(float progress) =>
		float.IsNaN(progress)
			? 0
			: float.Clamp(progress / 100.0f, 0, 1);
}
