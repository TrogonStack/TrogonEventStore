using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Tests.Services.Storage;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.Transforms.Identity;

namespace EventStore.Core.Tests.TransactionLog.Truncation;

public abstract class TruncateScenario<TLogFormat, TStreamId>(
	int maxEntriesInMemTable = 100,
	int metastreamMaxCount = 1) : ReadIndexTestScenario<TLogFormat, TStreamId>(maxEntriesInMemTable, metastreamMaxCount)
{
	protected TFChunkDbTruncator Truncator;
	protected long TruncateCheckpoint = long.MinValue;

	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		if (TruncateCheckpoint == long.MinValue)
			throw new InvalidOperationException("AckCheckpoint must be set in WriteTestScenario.");

		OnBeforeTruncating();

		// need to close db before truncator can delete files

		ReadIndex.Close();
		ReadIndex.Dispose();

		TableIndex.Close(removeFiles: false);

		await Db.DisposeAsync();

		var truncator = new TFChunkDbTruncator(Db.Config, _ => new IdentityChunkTransformFactory());
		await truncator.TruncateDb(TruncateCheckpoint, CancellationToken.None);
	}

	protected virtual void OnBeforeTruncating()
	{
	}
}
