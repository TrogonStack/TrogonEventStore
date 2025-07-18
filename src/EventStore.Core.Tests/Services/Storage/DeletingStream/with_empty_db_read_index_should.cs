using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WithEmptyDbReadIndexShould<TLogFormat, TStreamId> : ReadIndexTestScenario<TLogFormat, TStreamId>
{
	[Test]
	public void indicate_that_any_stream_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("X"), Is.False);
		Assert.That(ReadIndex.IsStreamDeleted("YY"), Is.False);
		Assert.That(ReadIndex.IsStreamDeleted("ZZZ"), Is.False);
	}
}
