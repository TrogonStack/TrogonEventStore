using System;
using System.Diagnostics.Tracing;
using EventStore.Core.Bus;
using Serilog;

namespace EventStore.Core.Metrics;

public class GcSuspensionMetric : EventListener
{
	private static readonly ILogger Log = Serilog.Log.ForContext<GcSuspensionMetric>();
	private static readonly TimeSpan LongSuspensionThreshold = InMemoryBus.DefaultSlowMessageThreshold;
	private static readonly TimeSpan VeryLongSuspensionThreshold = TimeSpan.FromMilliseconds(600);
	private static readonly TimeSpan LongSuspensionLogPeriod = TimeSpan.FromSeconds(10);
	private const int GcKeyword = 0x0000001;
	private const int GCStart = 1;
	private const int GCEnd = 2;
	private const int GCSuspendEEBegin = 9;
	private const int GCRestartEEEnd = 3;
	private readonly DurationMaxTracker _tracker;
	private DateTime? _fullGcStarted;
	private uint? _fullGcNumber;
	private DateTime? _suspendStarted;
	private GCSuspendReason? _suspendReason;
	private DateTime _lastLongSuspensionLog;
	private TimeSpan _periodLongSuspensionsElapsedTotal;
	private int _periodLongSuspensionCount;

	private enum GCStartReason : uint
	{
		SmallObjectHeapAllocation = 0,
		Induced = 1,
		LowMemory = 2,
		Empty = 3,
		LargeObjectHeapAllocation = 4,
		OutOfSpaceForSmallObjectHeap = 5,
		OutOfSpaceForLargeObjectHeap = 6,
		InducedButNotForcedAsBlocking = 7,
		StressTesting = 8,
		LowMemoryBlocking = 9,
		UserCodeInducedCompacting = 10,
	}

	private enum GCStartType : uint
	{
		BlockingOutsideBackgroundGc = 0,
		BackgroundGc = 1,
		BlockingDuringBackgroundGc = 2,
	}

	private enum GCSuspendReason : uint
	{
		Other = 0,
		GarbageCollection = 1,
		AppDomainShutdown = 2,
		CodePitching = 3,
		Shutdown = 4,
		Debugger = 5,
		GarbageCollectionPreparation = 6,
		DebuggerSweep = 7,
	}

	public GcSuspensionMetric(DurationMaxTracker tracker)
	{
		_tracker = tracker;
	}

	protected override void OnEventSourceCreated(EventSource eventSource)
	{
		if (eventSource.Name.Equals("Microsoft-Windows-DotNETRuntime"))
		{
			EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)GcKeyword);
		}
	}

	protected override void OnEventWritten(EventWrittenEventArgs eventData)
	{
		if (_tracker == null)
		{
			return;
		}

		switch (eventData.EventId)
		{
			case GCStart:
				{
					var payload = eventData.Payload!;
					var payloadNames = eventData.PayloadNames!;
					uint? gcNumber = null;
					uint? generation = null;
					GCStartReason? reason = null;
					GCStartType? type = null;

					for (var i = 0; i < payloadNames.Count; i++)
					{
						switch (payloadNames[i])
						{
							case "Count":
								gcNumber = (uint)payload[i]!;
								break;
							case "Depth":
								generation = (uint)payload[i]!;
								break;
							case "Reason":
								reason = (GCStartReason)payload[i]!;
								break;
							case "Type":
								type = (GCStartType)payload[i]!;
								break;
						}
					}

					if (generation >= 2 &&
						type is GCStartType.BlockingOutsideBackgroundGc or GCStartType.BlockingDuringBackgroundGc)
					{
						Log.Information(
							"Start of full blocking garbage collection at {TimeStamp}. GC: #{GCNumber}. Generation: {Generation}. Reason: {Reason}. Type: {Type}.",
							eventData.TimeStamp, gcNumber, generation, reason, type);

						_fullGcStarted = eventData.TimeStamp;
						_fullGcNumber = gcNumber;
					}

					break;
				}

			case GCEnd:
				{
					var gcNumber = (uint)eventData.Payload![eventData.PayloadNames!.IndexOf("Count")]!;

					if (gcNumber == _fullGcNumber && _fullGcStarted is { } started)
					{
						var elapsed = eventData.TimeStamp.Subtract(started);

						Log.Information(
							"End of full blocking garbage collection at {TimeStamp}. GC: #{GCNumber}. Took: {Elapsed:N0}ms",
							eventData.TimeStamp, gcNumber, elapsed.TotalMilliseconds);

						_fullGcStarted = null;
						_fullGcNumber = null;
					}

					break;
				}

			case GCSuspendEEBegin:
				{
					_suspendReason = (GCSuspendReason)eventData.Payload![eventData.PayloadNames!.IndexOf("Reason")]!;
					_suspendStarted = eventData.TimeStamp;
					break;
				}

			case GCRestartEEEnd:
				{
					if (_suspendStarted is not { } started)
					{
						Log.Warning(
							"Unexpected garbage collection GCRestartEEEnd event. Started: {Started}. Reason: {Reason}",
							_suspendStarted, _suspendReason);
						return;
					}

					var elapsed = eventData.TimeStamp.Subtract(started);
					if (_suspendReason is GCSuspendReason.GarbageCollection
						or GCSuspendReason.GarbageCollectionPreparation)
					{
						_tracker.RecordNow(elapsed);
					}

					if (elapsed >= VeryLongSuspensionThreshold)
					{
						Log.Warning(
							"Garbage collection: Very long Execution Engine Suspension. Reason: {Reason}. Took: {Elapsed:N0}ms",
							_suspendReason, elapsed.TotalMilliseconds);
					}
					else if (elapsed >= LongSuspensionThreshold)
					{
						var now = DateTime.Now;
						if (now - _lastLongSuspensionLog > LongSuspensionLogPeriod)
						{
							if (_periodLongSuspensionCount > 0)
							{
								Log.Information(
									"Garbage collection: Long Execution Engine Suspensions omitted. {Count} suspensions took {Elapsed:N0}ms each on average",
									_periodLongSuspensionCount,
									_periodLongSuspensionsElapsedTotal.TotalMilliseconds / _periodLongSuspensionCount);
							}

							Log.Information(
								"Garbage collection: Long Execution Engine Suspension. Reason: {Reason}. Took: {Elapsed:N0}ms",
								_suspendReason, elapsed.TotalMilliseconds);

							_lastLongSuspensionLog = now;
							_periodLongSuspensionCount = 0;
							_periodLongSuspensionsElapsedTotal = TimeSpan.Zero;
						}
						else
						{
							_periodLongSuspensionCount++;
							_periodLongSuspensionsElapsedTotal += elapsed;
						}
					}

					_suspendStarted = null;
					_suspendReason = null;
					break;
				}
		}
	}
}
