using System;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using TrogonEventStore.SemanticConventions;
using static EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core.Metrics;

public class IncomingGrpcCallsMetric : EventListener
{
	private long _callsCurrent;
	private long _callsTotal;
	private long _callsFailed;
	private long _callsUnimplemented;
	private long _callsDeadlineExceeded;

	public IncomingGrpcCallsMetric(
		Meter meter,
		MetricDefinition currentCallsDefinition,
		MetricDefinition totalCallsDefinition,
		MetricDefinition failedCallsDefinition,
		MetricDefinition unimplementedCallsDefinition,
		MetricDefinition deadlineExceededCallsDefinition,
		IncomingGrpcCall[] filter)
	{
		currentCallsDefinition.EnsureInstrumentKind(MetricInstrumentKind.UpDownCounter);
		totalCallsDefinition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
		failedCallsDefinition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
		unimplementedCallsDefinition.EnsureInstrumentKind(MetricInstrumentKind.Counter);
		deadlineExceededCallsDefinition.EnsureInstrumentKind(MetricInstrumentKind.Counter);

		if (filter.Contains(IncomingGrpcCall.Current))
		{
			meter.CreateObservableUpDownCounter(
				currentCallsDefinition.Name,
				() => Volatile.Read(ref _callsCurrent),
				currentCallsDefinition.Unit,
				currentCallsDefinition.Description);
		}

		CreateCounter(IncomingGrpcCall.Total, totalCallsDefinition, () => Volatile.Read(ref _callsTotal));
		CreateCounter(IncomingGrpcCall.Failed, failedCallsDefinition, () => Volatile.Read(ref _callsFailed));
		CreateCounter(
			IncomingGrpcCall.Unimplemented,
			unimplementedCallsDefinition,
			() => Volatile.Read(ref _callsUnimplemented));
		CreateCounter(
			IncomingGrpcCall.DeadlineExceeded,
			deadlineExceededCallsDefinition,
			() => Volatile.Read(ref _callsDeadlineExceeded));

		void CreateCounter(IncomingGrpcCall tracker, MetricDefinition definition, Func<long> observe)
		{
			if (filter.Contains(tracker))
			{
				meter.CreateObservableCounter(
					definition.Name,
					observe,
					definition.Unit,
					definition.Description);
			}
		}
	}

	protected override void OnEventSourceCreated(EventSource eventSource)
	{
		if (eventSource.Name == "Grpc.AspNetCore.Server")
		{
			EnableEvents(eventSource, EventLevel.Verbose);
		}
	}

	protected override void OnEventWritten(EventWrittenEventArgs eventData)
	{
		switch (eventData.EventName)
		{
			case "CallStart":
				Interlocked.Increment(ref _callsTotal);
				Interlocked.Increment(ref _callsCurrent);
				break;
			case "CallStop":
				Interlocked.Decrement(ref _callsCurrent);
				break;
			case "CallFailed":
				Interlocked.Increment(ref _callsFailed);
				break;
			case "CallDeadlineExceeded":
				Interlocked.Increment(ref _callsDeadlineExceeded);
				break;
			case "CallUnimplemented":
				Interlocked.Increment(ref _callsUnimplemented);
				break;
		}
	}
}
