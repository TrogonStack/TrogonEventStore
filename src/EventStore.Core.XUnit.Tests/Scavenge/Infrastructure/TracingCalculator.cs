using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingCalculator<TStreamId>(ICalculator<TStreamId> wrapped, Tracer tracer) : ICalculator<TStreamId>
{
	public async ValueTask Calculate(
		ScavengePoint scavengePoint,
		IScavengeStateForCalculator<TStreamId> source,
		CancellationToken cancellationToken)
	{

		tracer.TraceIn($"Calculating {scavengePoint.GetName()}");
		try
		{
			await wrapped.Calculate(scavengePoint, source, cancellationToken);
			tracer.TraceOut("Done");
		}
		catch
		{
			tracer.TraceOut("Exception calculating");
			throw;
		}
	}

	public async ValueTask Calculate(
		ScavengeCheckpoint.Calculating<TStreamId> checkpoint,
		IScavengeStateForCalculator<TStreamId> source,
		CancellationToken cancellationToken)
	{

		tracer.TraceIn($"Calculating from checkpoint: {checkpoint}");
		try
		{
			await wrapped.Calculate(checkpoint, source, cancellationToken);
			tracer.TraceOut("Done");
		}
		catch
		{
			tracer.TraceOut("Exception calculating");
			throw;
		}
	}
}
