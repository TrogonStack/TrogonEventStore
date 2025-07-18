using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenDeletingStreamWithVersionNumbersGreaterThanIntMaxvalue<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	long firstEventNumber = (long)int.MaxValue + 1;
	long secondEventNumber = (long)int.MaxValue + 2;
	long thirdEventNumber = (long)int.MaxValue + 3;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent("ES", firstEventNumber, new string('.', 3000), token: token);
		await WriteSingleEvent("KEEP", firstEventNumber, new string('.', 3000), token: token);
		await WriteSingleEvent("KEEP", secondEventNumber, new string('.', 3000), token: token);
		await WriteSingleEvent("ES", secondEventNumber, new string('.', 3000), retryOnFail: true, token: token);
		await WriteSingleEvent("KEEP", thirdEventNumber, new string('.', 3000), token: token);
		await WriteSingleEvent("ES", thirdEventNumber, new string('.', 3000), token: token);

		await WriteDelete("ES", token);
	}

	[Test]
	public void indicate_that_stream_is_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("ES"));
	}

	[Test]
	public void indicate_that_other_stream_is_not_deleted()
	{
		Assert.IsFalse(ReadIndex.IsStreamDeleted("KEEP"));
	}
}
