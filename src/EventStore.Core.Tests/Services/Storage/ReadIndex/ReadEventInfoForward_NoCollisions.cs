using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.Index.Hashers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.ReadIndex;

[TestFixture]
public abstract class ReadEventInfoForward_NoCollisions() : ReadIndexTestScenario<LogFormat.V2, string>(
	maxEntriesInMemTable: 3,
	lowHasher: new ConstantHasher(0),
	highHasher: new HumanReadableHasher32())
{
	private const string Stream = "ab-1";
	private const ulong Hash = 98;
	private const string NonCollidingStream = "cd-1";

	private static void CheckResult(EventRecord[] events, IndexReadEventInfoResult result)
	{
		Assert.AreEqual(events.Length, result.EventInfos.Length);
		for (int i = 0; i < events.Length; i++)
		{
			Assert.AreEqual(events[i].EventNumber, result.EventInfos[i].EventNumber);
			Assert.AreEqual(events[i].LogPosition, result.EventInfos[i].LogPosition);
		}
	}

	public class VerifyNoCollision : ReadEventInfoForward_NoCollisions
	{
		[Test]
		public void verify_that_streams_do_not_collide()
		{
			Assert.AreNotEqual(Hasher.Hash(Stream), Hasher.Hash(NonCollidingStream));
		}
	}

	public class WithNoEvents : ReadEventInfoForward_NoCollisions
	{
		[Test]
		public void with_no_events()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				0,
				int.MaxValue,
				long.MaxValue);

			Assert.AreEqual(0, result.EventInfos.Length);
			Assert.True(result.IsEndOfStream);
		}
	}

	public class WithOneEvent : ReadEventInfoForward_NoCollisions
	{
		private EventRecord _event;

		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			_event = await WriteSingleEvent(Stream, 0, "test data", token: token);
		}

		[Test]
		public void with_one_event()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				0,
				int.MaxValue,
				long.MaxValue);

			Assert.AreEqual(1, result.EventInfos.Length);
			Assert.AreEqual(int.MaxValue, result.NextEventNumber);
			CheckResult(new[] { _event }, result);

			result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				1,
				int.MaxValue,
				long.MaxValue);
			Assert.True(result.IsEndOfStream);
			CheckResult([], result);
		}
	}

	public class WithMultipleEvents : ReadEventInfoForward_NoCollisions
	{
		private readonly List<EventRecord> _events = [];

		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			// PTable 1
			await WriteSingleEvent(NonCollidingStream, 0, string.Empty, token: token);
			await WriteSingleEvent(NonCollidingStream, 1, string.Empty, token: token);
			_events.Add(await WriteSingleEvent(Stream, 0, string.Empty, token: token));

			// PTable 2
			_events.Add(await WriteSingleEvent(Stream, 1, string.Empty, token: token));
			_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token));
			await WriteSingleEvent(NonCollidingStream, 2, string.Empty, token: token);

			// MemTable
			_events.Add(await WriteSingleEvent(Stream, 3, string.Empty, token: token));
			await WriteSingleEvent(NonCollidingStream, 3, string.Empty, token: token);
		}

		[Test]
		public void with_multiple_events()
		{
			for (int fromEventNumber = 0; fromEventNumber <= 4; fromEventNumber++)
			{
				var result = ReadIndex.ReadEventInfoForward_NoCollisions(
					Hash,
					fromEventNumber,
					int.MaxValue,
					long.MaxValue);

				CheckResult(_events.Skip(fromEventNumber).ToArray(), result);

				if (fromEventNumber > 3)
					Assert.True(result.IsEndOfStream);
				else
					Assert.AreEqual((long)fromEventNumber + int.MaxValue, result.NextEventNumber);
			}
		}

		[Test]
		public void with_multiple_events_and_max_count()
		{
			for (int fromEventNumber = 0; fromEventNumber <= 4; fromEventNumber++)
			{
				var result = ReadIndex.ReadEventInfoForward_NoCollisions(
					Hash,
					fromEventNumber,
					2,
					long.MaxValue);

				CheckResult(_events.Skip(fromEventNumber).Take(2).ToArray(), result);
				if (fromEventNumber > 3)
					Assert.True(result.IsEndOfStream);
				else
					Assert.AreEqual(fromEventNumber + 2, result.NextEventNumber);
			}
		}

		[Test]
		public void with_multiple_events_and_before_position()
		{
			for (int fromEventNumber = 0; fromEventNumber + 1 < _events.Count; fromEventNumber++)
			{
				var result = ReadIndex.ReadEventInfoForward_NoCollisions(
					Hash,
					fromEventNumber,
					int.MaxValue,
					_events[fromEventNumber + 1].LogPosition);

				CheckResult(_events.Skip(fromEventNumber).Take(1).ToArray(), result);
				Assert.AreEqual((long)fromEventNumber + int.MaxValue, result.NextEventNumber);
			}
		}
	}

	public class WithDeletedStream : ReadEventInfoForward_NoCollisions
	{
		private readonly List<EventRecord> _events = [];

		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			_events.Add(await WriteSingleEvent(Stream, 0, "test data", token: token));
			_events.Add(await WriteSingleEvent(Stream, 1, "test data", token: token));

			var prepare = await WriteDeletePrepare(Stream, token);
			await WriteDeleteCommit(prepare, token);
		}

		[Test]
		public void can_read_events_and_tombstone_event_not_returned()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				0,
				int.MaxValue,
				long.MaxValue);

			CheckResult(_events.ToArray(), result);
			Assert.AreEqual(int.MaxValue, result.NextEventNumber);
		}

		[Test]
		public void next_event_number_set_correctly()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				2,
				int.MaxValue,
				long.MaxValue);

			Assert.AreEqual(0, result.EventInfos.Length);
			Assert.AreEqual(EventNumber.DeletedStream, result.NextEventNumber);
		}

		[Test]
		public void can_read_tombstone_event()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				EventNumber.DeletedStream,
				int.MaxValue,
				long.MaxValue);

			Assert.AreEqual(1, result.EventInfos.Length);
			Assert.AreEqual(EventNumber.DeletedStream, result.EventInfos[0].EventNumber);
			Assert.True(result.IsEndOfStream);
		}
	}

	public class WithGapsBetweenEvents : ReadEventInfoForward_NoCollisions
	{
		private readonly List<EventRecord> _events = [];

		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			// PTable 1
			await WriteSingleEvent(NonCollidingStream, 0, string.Empty, token: token);
			await WriteSingleEvent(NonCollidingStream, 1, string.Empty, token: token);
			_events.Add(await WriteSingleEvent(Stream, 0, string.Empty, token: token));

			// PTable 2
			_events.Add(await WriteSingleEvent(Stream, 4, string.Empty, token: token));
			_events.Add(await WriteSingleEvent(Stream, 5, string.Empty, token: token));
			await WriteSingleEvent(NonCollidingStream, 2, string.Empty, token: token);

			// MemTable
			_events.Add(await WriteSingleEvent(Stream, 11, string.Empty, token: token));
			await WriteSingleEvent(NonCollidingStream, 3, string.Empty, token: token);
		}

		[Test]
		public void strictly_returns_up_to_max_count_consecutive_events_from_start_event_number()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				0,
				int.MaxValue,
				long.MaxValue);

			CheckResult(_events.ToArray(), result);
			Assert.AreEqual(int.MaxValue, result.NextEventNumber);

			result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				0,
				3,
				long.MaxValue);

			CheckResult(_events.Take(1).ToArray(), result);
			Assert.AreEqual(3, result.NextEventNumber);

			result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				3,
				int.MaxValue,
				long.MaxValue);

			CheckResult(_events.Skip(1).ToArray(), result);
			Assert.AreEqual((long)3 + int.MaxValue, result.NextEventNumber);

			result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				4,
				3,
				long.MaxValue);

			CheckResult(_events.Skip(1).Take(2).ToArray(), result);
			Assert.AreEqual(7, result.NextEventNumber);

			result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				7,
				3,
				long.MaxValue);

			Assert.AreEqual(0, result.EventInfos.Length);
			Assert.AreEqual(11, result.NextEventNumber);
		}
	}


	public class WithDuplicateEvents : ReadEventInfoForward_NoCollisions
	{
		private readonly List<EventRecord> _events = [];

		protected override async ValueTask WriteTestScenario(CancellationToken token)
		{
			// PTable 1
			await WriteSingleEvent(NonCollidingStream, 0, string.Empty, token: token);
			await WriteSingleEvent(NonCollidingStream, 1, string.Empty, token: token);
			_events.Add(await WriteSingleEvent(Stream, 0, string.Empty, token: token));

			// PTable 2
			_events.Add(await WriteSingleEvent(Stream, 1, string.Empty, token: token));
			_events.Add(await WriteSingleEvent(Stream, 1, string.Empty, token: token));
			await WriteSingleEvent(NonCollidingStream, 2, string.Empty, token: token);

			// MemTable
			_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token));
			_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token));
			_events.Add(await WriteSingleEvent(Stream, 2, string.Empty, token: token));
		}

		[Test]
		public void result_is_deduplicated_keeping_oldest_duplicates()
		{
			var result = ReadIndex.ReadEventInfoForward_NoCollisions(
				Hash,
				0,
				int.MaxValue,
				long.MaxValue);

			CheckResult(
				_events
					.GroupBy(x => x.EventNumber)
					.Select(x => x.First())
					.OrderBy(x => x.EventNumber)
					.ToArray(),
				result);
			Assert.AreEqual(int.MaxValue, result.NextEventNumber);
		}
	}
}
