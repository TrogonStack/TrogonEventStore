using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Projections.Core.Services;

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
			var projectionRunning = HasStatus(x.Status, "running")
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

			if (HasStatus(statistics.Status, "running"))
			{
				projectionRunning = 1;
			}
			else if (HasStatus(statistics.Status, "stopped"))
			{
				projectionStopped = 1;
			}
			else if (HasStatus(statistics.Status, "faulted"))
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

	private static bool HasStatus(string status, string expected) =>
		status?.StartsWith(expected, StringComparison.OrdinalIgnoreCase) == true;
}
