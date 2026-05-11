using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;

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
					new("projection", x.Name)
				]));

	public IEnumerable<Measurement<float>> ObserveProgress() =>
		_currentStats.Select(x =>
			new Measurement<float>(
				x.Progress / 100.0f,
				[
					new("projection", x.Name)
				]));

	public IEnumerable<Measurement<long>> ObserveRunning() =>
		_currentStats.Select(x =>
		{
			var projectionRunning = x.LeaderStatus == ManagedProjectionState.Running
				? 1
				: 0;

			return new Measurement<long>(
				projectionRunning, [
					new("projection", x.Name)
				]);
		});

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
				new("projection", statistics.Name),
				new("status", "Running"),
			]);

			yield return new(projectionFaulted, [
				new("projection", statistics.Name),
				new("status", "Faulted"),
			]);

			yield return new(projectionStopped, [
				new("projection", statistics.Name),
				new("status", "Stopped"),
			]);
		}
	}
}
