using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Tests.Index.Hashers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.HashCollisions;

[TestFixture]
public abstract class GetStreamLastEventNumber_NoCollisions() : ReadIndexTestScenario<LogFormat.V2, string>(
	maxEntriesInMemTable: 3,
	lowHasher: new ConstantHasher(0),
	highHasher: new HumanReadableHasher32())
{
	private const string Stream = "ab-1";
	private const ulong Hash = 98;
	private const string NonCollidingStream = "cd-1";

	private string GetStreamId(ulong hash) => hash == Hash ? Stream : throw new ArgumentException();

	public class VerifyNoCollision : GetStreamLastEventNumber_NoCollisions
	{
		[Test]
		public void verify_that_streams_do_not_collide()
		{
			Assert.AreNotEqual(Hasher.Hash(Stream), Hasher.Hash(NonCollidingStream));
		}
	}

	public class WithNoEvents : GetStreamLastEventNumber_NoCollisions
	{
		[Test]
		public void with_no_events()
		{
			Assert.AreEqual(ExpectedVersion.NoStream,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					long.MaxValue));
		}
	}

	public class WithOneEvent : GetStreamLastEventNumber_NoCollisions
	{
		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			await WriteSingleEvent(Stream, 0, "test data", token: token);
		}

		[Test]
		public void with_one_event()
		{
			Assert.AreEqual(0,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					long.MaxValue));
		}
	}

	public class WithMultipleEvents : GetStreamLastEventNumber_NoCollisions
	{
		private EventRecord _zeroth, _first, _second, _third;

		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			// PTable 1
			await WriteSingleEvent(NonCollidingStream, 0, string.Empty, token: token);
			await WriteSingleEvent(NonCollidingStream, 1, string.Empty, token: token);
			_zeroth = await WriteSingleEvent(Stream, 0, string.Empty, token: token);

			// PTable 2
			_first = await WriteSingleEvent(Stream, 1, string.Empty, token: token);
			_second = await WriteSingleEvent(Stream, 2, string.Empty, token: token);
			await WriteSingleEvent(NonCollidingStream, 2, string.Empty, token: token);

			// MemTable
			_third = await WriteSingleEvent(Stream, 3, string.Empty, token: token);
			await WriteSingleEvent(NonCollidingStream, 3, string.Empty, token: token);
		}

		[Test]
		public void with_multiple_events()
		{
			Assert.AreEqual(3,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					long.MaxValue));
		}

		[Test]
		public void with_multiple_events_and_before_position()
		{
			Assert.AreEqual(3,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					_third.LogPosition + 1));

			Assert.AreEqual(2,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					_third.LogPosition));

			Assert.AreEqual(1,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					_second.LogPosition));

			Assert.AreEqual(0,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					_first.LogPosition));

			Assert.AreEqual(ExpectedVersion.NoStream,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					_zeroth.LogPosition));
		}
	}

	public class WithDeletedStream : GetStreamLastEventNumber_NoCollisions
	{
		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			await WriteSingleEvent(Stream, 0, "test data", token: token);
			await WriteSingleEvent(Stream, 1, "test data", token: token);

			var prepare = await WriteDeletePrepare(Stream, token);
			await WriteDeleteCommit(prepare, token);
		}

		[Test]
		public void with_deleted_stream()
		{
			Assert.AreEqual(EventNumber.DeletedStream,
				ReadIndex.GetStreamLastEventNumber_NoCollisions(
					Hash,
					GetStreamId,
					long.MaxValue));
		}
	}
}
