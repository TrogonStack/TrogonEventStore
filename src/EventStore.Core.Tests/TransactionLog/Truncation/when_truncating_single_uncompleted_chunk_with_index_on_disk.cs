using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog.Truncation;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_truncating_single_uncompleted_chunk_with_index_on_disk<TLogFormat, TStreamId>()
	: TruncateScenario<TLogFormat, TStreamId>(maxEntriesInMemTable: 3)
{
	private EventRecord _event2;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent("ES", 0, new string('.', 500), token: token);
		_event2 = await WriteSingleEvent("ES", 1, new string('.', 500), token: token);
		await WriteSingleEvent("ES", 2, new string('.', 500), token: token); // index goes to disk
		await WriteSingleEvent("ES", 3, new string('.', 500), token: token);

		TruncateCheckpoint = _event2.LogPosition;
	}

	[Test]
	public void checksums_should_be_equal_to_ack_checksum()
	{
		Assert.AreEqual(TruncateCheckpoint, WriterCheckpoint.Read());
		Assert.AreEqual(TruncateCheckpoint, ChaserCheckpoint.Read());
	}
}
