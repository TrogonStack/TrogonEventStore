using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Reflection;
using EventStore.Common.Options;
using EventStore.Core.Diagnostics;
using EventStore.Projections.Core.Metrics;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Management;
using TrogonEventStore.SemanticConventions;
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
		AssertMeasurement(50L, (TrogonAttributeNames.ProjectionName, "TestProjection"))(measurement);
	}

	[Theory]
	[InlineData(-10, 0)]
	[InlineData(0, 0)]
	[InlineData(75, 0.75f)]
	[InlineData(100, 1)]
	[InlineData(150, 1)]
	public void ObserveProgressAsFraction(float progress, float expected)
	{
		_sut.OnNewStats([ProjectionWithProgress(progress)]);

		var measurements = _sut.ObserveProgress();
		var measurement = Assert.Single(measurements);
		AssertMeasurement(expected, (TrogonAttributeNames.ProjectionName, "TestProjection"))(measurement);
	}

	[Fact]
	public void ObserveProgressTreatsUnknownProgressAsZero()
	{
		_sut.OnNewStats([ProjectionWithProgress(float.NaN)]);

		var measurement = Assert.Single(_sut.ObserveProgress());
		Assert.Equal(0, measurement.Value);
	}

	[Fact]
	public void ObserveStatus()
	{
		var measurements = _sut.ObserveStatus();
		Assert.Collection(measurements,
			AssertMeasurement(1L,
				(TrogonAttributeNames.ProjectionName, "TestProjection"),
				(TrogonAttributeNames.ProjectionStatus, "running")),
			AssertMeasurement(0L,
				(TrogonAttributeNames.ProjectionName, "TestProjection"),
				(TrogonAttributeNames.ProjectionStatus, "faulted")),
			AssertMeasurement(0L,
				(TrogonAttributeNames.ProjectionName, "TestProjection"),
				(TrogonAttributeNames.ProjectionStatus, "stopped")));
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
			AssertMeasurement(running,
				(TrogonAttributeNames.ProjectionName, "TestProjection"),
				(TrogonAttributeNames.ProjectionStatus, "running")),
			AssertMeasurement(faulted,
				(TrogonAttributeNames.ProjectionName, "TestProjection"),
				(TrogonAttributeNames.ProjectionStatus, "faulted")),
			AssertMeasurement(stopped,
				(TrogonAttributeNames.ProjectionName, "TestProjection"),
				(TrogonAttributeNames.ProjectionStatus, "stopped")));
	}

	[Fact]
	public void ObserveStateSizes()
	{
		_sut.OnNewStats([new() {
			Name = "TestProjection",
			StateSizes = new Dictionary<string, int> {
				[string.Empty] = 10,
				["test-partition"] = 12,
			},
		}]);

		var measurements = _sut.ObserveStateSize();

		var measurement = Assert.Single(measurements);
		AssertMeasurement(22L, (TrogonAttributeNames.ProjectionName, "TestProjection"))(measurement);
	}

	[Fact]
	public void ObserveStateSizesWithoutStateSizes()
	{
		var measurements = _sut.ObserveStateSize();

		Assert.Empty(measurements);
	}

	[Fact]
	public void ProjectionInstrumentsMatchCanonicalDefinitions()
	{
		var instruments = new List<Instrument>();
		using var listener = new MeterListener
		{
			InstrumentPublished = (instrument, _) =>
			{
				if (instrument.Meter.Name == TelemetryMeterInstrumentation.ProjectionsName)
				{
					instruments.Add(instrument);
				}
			},
		};
		listener.Start();

		var subsystem = new ProjectionsSubsystem(new ProjectionSubsystemOptions(
			1,
			ProjectionType.None,
			false,
			TimeSpan.Zero,
			false,
			0,
			0));
		var configureMetrics = typeof(ProjectionsSubsystem).GetMethod(
			"ConfigureProjectionMetrics",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(configureMetrics);
		configureMetrics.Invoke(subsystem, [true]);

		AssertInstrument<ObservableCounter<long>>(
			instruments,
			MetricDefinitions.TrogonEventstoreProjectionEventProcessedCount);
		AssertInstrument<ObservableGauge<float>>(
			instruments,
			MetricDefinitions.TrogonEventstoreProjectionProgress);
		AssertInstrument<ObservableUpDownCounter<long>>(
			instruments,
			MetricDefinitions.TrogonEventstoreProjectionStatus);
		AssertInstrument<ObservableUpDownCounter<long>>(
			instruments,
			MetricDefinitions.TrogonEventstoreProjectionStateSize);
		Assert.DoesNotContain(instruments, instrument =>
			instrument.Name.Contains("projection.running", StringComparison.Ordinal) ||
			instrument.Name.Contains("projection-running", StringComparison.Ordinal));
	}

	static Action<Measurement<T>> AssertMeasurement<T>(
		T expectedValue, params (string, string?)[] tags) where T : struct =>

		actualMeasurement =>
		{
			Assert.Equal(expectedValue, actualMeasurement.Value);

			Assert.Equal(
				tags,
				actualMeasurement.Tags.ToArray().Select(tag => (tag.Key, tag.Value as string)));
		};

	private static void AssertInstrument<TInstrument>(
		IEnumerable<Instrument> instruments,
		MetricDefinition definition)
		where TInstrument : Instrument
	{
		var instrument = Assert.Single(instruments, candidate => candidate.Name == definition.Name);
		Assert.IsType<TInstrument>(instrument);
		Assert.Equal(definition.Unit, instrument.Unit);
		Assert.Equal(definition.Description, instrument.Description);
	}

	private static ProjectionStatistics ProjectionWithState(ManagedProjectionState state, string status) =>
		new()
		{
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

	private static ProjectionStatistics ProjectionWithProgress(float progress) =>
		new()
		{
			Name = "TestProjection",
			Progress = progress,
		};
}
