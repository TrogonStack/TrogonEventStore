using EventStore.Core.TransactionLog;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class read_results_should
{
	[Test]
	public void report_eof_for_failed_sequential_reads()
	{
		Assert.Multiple(() =>
		{
			Assert.False(SeqReadResult.Failure.Success);
			Assert.True(SeqReadResult.Failure.Eof);
			Assert.AreEqual(-1, SeqReadResult.Failure.RecordPrePosition);
			Assert.AreEqual(-1, SeqReadResult.Failure.RecordPostPosition);
		});
	}
}
