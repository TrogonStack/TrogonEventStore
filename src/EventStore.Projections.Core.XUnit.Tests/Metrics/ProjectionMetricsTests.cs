using System.Diagnostics.Metrics;
using EventStore.Projections.Core.Metrics;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;
using Xunit;

namespace EventStore.Projections.Core.XUnit.Tests.Metrics;

public class ProjectionMetricsTests
{
	readonly ProjectionTracker _sut = new();

	public ProjectionMetricsTests()
	{
		_sut.OnNewStats([new() {
			Name = "TestProjection",
			ProjectionId = 1234,
			Epoch = -1,
			Version = -1,
			Mode = ProjectionMode.Continuous,
			Status = "Running",
			LeaderStatus = ManagedProjectionState.Running,
			Progress = 75,
			EventsProcessedAfterRestart = 50,
		}]);
	}

	[Fact]
	public void ObserveEventsProcessed()
	{
		var measurements = _sut.ObserveEventsProcessed();
		var measurement = Assert.Single(measurements);
		AssertMeasurement(50L, ("projection", "TestProjection"))(measurement);
	}

	[Fact]
	public void ObserveRunning()
	{
		var measurements = _sut.ObserveRunning();
		var measurement = Assert.Single(measurements);
		AssertMeasurement(1L, ("projection", "TestProjection"))(measurement);
	}

	[Fact]
	public void ObserveRunningWithCompoundStatus()
	{
		_sut.OnNewStats([ProjectionWithState(ManagedProjectionState.Running, "Running/Writing results")]);

		var measurements = _sut.ObserveRunning();
		var measurement = Assert.Single(measurements);
		AssertMeasurement(1L, ("projection", "TestProjection"))(measurement);
	}

	[Fact]
	public void ObserveProgress()
	{
		var measurements = _sut.ObserveProgress();
		var measurement = Assert.Single(measurements);
		AssertMeasurement(0.75f, ("projection", "TestProjection"))(measurement);
	}

	[Fact]
	public void ObserveStatus()
	{
		var measurements = _sut.ObserveStatus();
		Assert.Collection(measurements,
			AssertMeasurement(1L, ("projection", "TestProjection"), ("status", "Running")),
			AssertMeasurement(0L, ("projection", "TestProjection"), ("status", "Faulted")),
			AssertMeasurement(0L, ("projection", "TestProjection"), ("status", "Stopped")));
	}

	[Theory]
	[InlineData(ManagedProjectionState.Running, "Running/Writing results", 1, 0, 0)]
	[InlineData(ManagedProjectionState.Faulted, "Faulted (Enabled)", 0, 1, 0)]
	[InlineData(ManagedProjectionState.Stopped, "Stopped (Enabled)", 0, 0, 1)]
	public void ObserveStatusWithCompoundStatus(ManagedProjectionState state, string status, long running,
		long faulted, long stopped)
	{
		_sut.OnNewStats([ProjectionWithState(state, status)]);

		var measurements = _sut.ObserveStatus();
		Assert.Collection(measurements,
			AssertMeasurement(running, ("projection", "TestProjection"), ("status", "Running")),
			AssertMeasurement(faulted, ("projection", "TestProjection"), ("status", "Faulted")),
			AssertMeasurement(stopped, ("projection", "TestProjection"), ("status", "Stopped")));
	}

	static Action<Measurement<T>> AssertMeasurement<T>(
		T expectedValue, params (string, string?)[] tags) where T : struct =>

		actualMeasurement =>
		{
			Assert.Equal(expectedValue, actualMeasurement.Value);
			if (actualMeasurement.Tags == null)
				return;

			Assert.Equal(
				tags,
				actualMeasurement.Tags.ToArray().Select(tag => (tag.Key, tag.Value as string)));
		};

	private static ProjectionStatistics ProjectionWithState(ManagedProjectionState state, string status) =>
		new() {
			Name = "TestProjection",
			ProjectionId = 1234,
			Epoch = -1,
			Version = -1,
			Mode = ProjectionMode.Continuous,
			Status = status,
			LeaderStatus = state,
			Progress = 75,
			EventsProcessedAfterRestart = 50,
		};
}
