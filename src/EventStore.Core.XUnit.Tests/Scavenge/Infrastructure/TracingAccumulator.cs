using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingAccumulator<TStreamId>(IAccumulator<TStreamId> wrapped, Tracer tracer) : IAccumulator<TStreamId>
{
	public async ValueTask Accumulate(
		ScavengePoint prevScavengePoint,
		ScavengePoint scavengePoint,
		IScavengeStateForAccumulator<TStreamId> state,
		CancellationToken cancellationToken)
	{

		tracer.TraceIn($"Accumulating from {prevScavengePoint?.GetName() ?? "start"} to {scavengePoint.GetName()}");
		try
		{
			await wrapped.Accumulate(prevScavengePoint, scavengePoint, state, cancellationToken);
			tracer.TraceOut("Done");
		}
		catch
		{
			tracer.TraceOut("Exception accumulating");
			throw;
		}
	}

	public async ValueTask Accumulate(
		ScavengeCheckpoint.Accumulating checkpoint,
		IScavengeStateForAccumulator<TStreamId> state,
		CancellationToken cancellationToken)
	{

		tracer.TraceIn($"Accumulating from checkpoint: {checkpoint}");
		try
		{
			await wrapped.Accumulate(checkpoint, state, cancellationToken);
			tracer.TraceOut("Done");
		}
		catch
		{
			tracer.TraceOut("Exception accumulating");
			throw;
		}
	}
}
