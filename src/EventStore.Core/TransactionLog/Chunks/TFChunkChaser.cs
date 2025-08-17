using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.TransactionLog.Checkpoint;

namespace EventStore.Core.TransactionLog.Chunks;

public class TFChunkChaser : ITransactionFileChaser
{
	public ICheckpoint Checkpoint
	{
		get { return _chaserCheckpoint; }
	}

	private readonly ICheckpoint _chaserCheckpoint;
	private readonly TFChunkReader _reader;

	public TFChunkChaser(TFChunkDb db, IReadOnlyCheckpoint writerCheckpoint, ICheckpoint chaserCheckpoint)
	{
		Ensure.NotNull(db, "dbConfig");
		Ensure.NotNull(writerCheckpoint, "writerCheckpoint");
		Ensure.NotNull(chaserCheckpoint, "chaserCheckpoint");

		_chaserCheckpoint = chaserCheckpoint;
		_reader = new TFChunkReader(db, writerCheckpoint, _chaserCheckpoint.Read());
	}

	public void Open()
	{
		// NOOP
	}

	public async ValueTask<SeqReadResult> TryReadNext(CancellationToken token)
	{
		var res = await _reader.TryReadNext(token);
		if (res.Success)
			_chaserCheckpoint.Write(res.RecordPostPosition);
		else
			_chaserCheckpoint.Write(_reader.CurrentPosition);

		return res;
	}

	public void Dispose()
	{
		Close();
	}

	public void Close()
	{
		Flush();
	}

	public void Flush()
	{
		_chaserCheckpoint.Flush();
	}
}
