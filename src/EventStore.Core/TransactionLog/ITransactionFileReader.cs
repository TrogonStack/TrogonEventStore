using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.DataStructures;

namespace EventStore.Core.TransactionLog;

public interface ITransactionFileReader
{
	void Reposition(long position);

	ValueTask<SeqReadResult> TryReadNext(CancellationToken token);
	ValueTask<SeqReadResult> TryReadPrev(CancellationToken token);

	ValueTask<RecordReadResult> TryReadAt(long position, bool couldBeScavenged, CancellationToken token);
	ValueTask<bool> ExistsAt(long position, CancellationToken token);
}

public struct TFReaderLease : IDisposable
{
	public readonly ITransactionFileReader Reader;
	private readonly ObjectPool<ITransactionFileReader> _pool;

	public TFReaderLease(ObjectPool<ITransactionFileReader> pool)
	{
		_pool = pool;
		Reader = pool.Get();
	}

	public TFReaderLease(ITransactionFileReader reader)
	{
		_pool = null;
		Reader = reader;
	}

	void IDisposable.Dispose() => _pool?.Return(Reader);

	public void Reposition(long position) => Reader.Reposition(position);

	public ValueTask<SeqReadResult> TryReadNext(CancellationToken token) => Reader.TryReadNext(token);

	public ValueTask<SeqReadResult> TryReadPrev(CancellationToken token) => Reader.TryReadPrev(token);

	public ValueTask<bool> ExistsAt(long position, CancellationToken token) => Reader.ExistsAt(position, token);

	public ValueTask<RecordReadResult> TryReadAt(long position, bool couldBeScavenged, CancellationToken token)
		=> Reader.TryReadAt(position, couldBeScavenged, token);
}
