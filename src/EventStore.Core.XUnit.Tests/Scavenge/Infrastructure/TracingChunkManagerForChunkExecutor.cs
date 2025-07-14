using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Scavenging;

namespace EventStore.Core.XUnit.Tests.Scavenge;

public class TracingChunkManagerForChunkExecutor<TStreamId, TRecord> :
	IChunkManagerForChunkExecutor<TStreamId, TRecord>
{

	private readonly IChunkManagerForChunkExecutor<TStreamId, TRecord> _wrapped;
	private readonly Tracer _tracer;

	public TracingChunkManagerForChunkExecutor(
		IChunkManagerForChunkExecutor<TStreamId, TRecord> wrapped, Tracer tracer)
	{

		_wrapped = wrapped;
		_tracer = tracer;
	}

	public async ValueTask<IChunkWriterForExecutor<TStreamId, TRecord>> CreateChunkWriter(
		IChunkReaderForExecutor<TStreamId, TRecord> sourceChunk,
		CancellationToken token)
	{

		return new TracingChunkWriterForExecutor<TStreamId, TRecord>(
			await _wrapped.CreateChunkWriter(sourceChunk, token),
			_tracer);
	}

	public IChunkReaderForExecutor<TStreamId, TRecord> GetChunkReaderFor(long position)
	{
		var reader = _wrapped.GetChunkReaderFor(position);
		return new TrackingChunkReaderForExecutor<TStreamId, TRecord>(reader, _tracer);
	}
}
