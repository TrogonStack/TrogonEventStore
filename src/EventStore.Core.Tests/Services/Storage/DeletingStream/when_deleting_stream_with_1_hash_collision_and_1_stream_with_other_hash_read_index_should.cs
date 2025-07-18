using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.DeletingStream;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	WhenDeletingStreamWith1HashCollisionAnd1StreamWithOtherHashReadIndexShould<TLogFormat, TStreamId> :
	ReadIndexTestScenario<TLogFormat, TStreamId>
{
	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		await WriteSingleEvent("S1", 0, "bla1", token: token);
		await WriteSingleEvent("S1", 1, "bla1", token: token);
		await WriteSingleEvent("S2", 0, "bla1", token: token);
		await WriteSingleEvent("S2", 1, "bla1", token: token);
		await WriteSingleEvent("S1", 2, "bla1", token: token);
		await WriteSingleEvent("SSS", 0, "bla1", token: token);

		await WriteDelete("S1", token);
	}

	[Test]
	public void indicate_that_stream_is_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("S1"));
	}

	[Test]
	public void indicate_that_other_stream_with_same_hash_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("S2"), Is.False);
	}

	[Test]
	public void indicate_that_other_stream_with_different_hash_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("SSS"), Is.False);
	}

	[Test]
	public void indicate_that_not_existing_stream_with_same_hash_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("XX"), Is.False);
	}

	[Test]
	public void indicate_that_not_existing_stream_with_different_hash_is_not_deleted()
	{
		Assert.That(ReadIndex.IsStreamDeleted("XXX"), Is.False);
	}
}
