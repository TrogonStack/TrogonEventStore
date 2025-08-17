using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WithEmptyDbReadIndexShould<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	[Test]
	public async Task indicate_that_any_stream_is_not_deleted()
	{
		Assert.That(await ReadIndex.IsStreamDeleted("X", CancellationToken.None), Is.False);
		Assert.That(await ReadIndex.IsStreamDeleted("YY", CancellationToken.None), Is.False);
		Assert.That(await ReadIndex.IsStreamDeleted("ZZZ", CancellationToken.None), Is.False);
	}
}
