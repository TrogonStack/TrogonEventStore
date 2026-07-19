using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Enumerators;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Enumerators;

[TestFixture]
public partial class EnumeratorTests
{
	private static EnumeratorWrapper CreateAllSubscription(
		IPublisher publisher,
		Position? checkpoint,
		ClaimsPrincipal user = null,
		CancellationToken cancellationToken = default)
	{

		return new EnumeratorWrapper(new Enumerator.AllSubscription(
			bus: publisher,
			expiryStrategy: new DefaultExpiryStrategy(),
			checkpoint: checkpoint,
			resolveLinks: false,
			user: user ?? SystemAccounts.System,
			requiresLeader: false,
			cancellationToken: cancellationToken));
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class subscribe_all_from_start<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
	{
		private readonly List<Guid> _eventIds = new();

		protected override void Given()
		{
			EnableReadAll();
			_eventIds.Add(WriteEvent("test-stream", "type1", "{}", "{Data: 1}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream", "type2", "{}", "{Data: 2}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream", "type3", "{}", "{Data: 3}").Item1.EventId);
		}

		[Test]
		public async Task should_receive_live_caught_up_message_after_reading_existing_events()
		{
			await using var sub = CreateAllSubscription(_publisher, checkpoint: null);

			Assert.True(await sub.GetNext() is SubscriptionConfirmation);
			Assert.AreEqual(_eventIds[0], ((Event)await sub.GetNext()).Id);
			Assert.AreEqual(_eventIds[1], ((Event)await sub.GetNext()).Id);
			var lastEvent = (Event)await sub.GetNext();
			Assert.AreEqual(_eventIds[2], lastEvent.Id);

			var caughtUp = (CaughtUp)await sub.GetNext();
			Assert.AreEqual((ulong)lastEvent.EventPosition!.Value.CommitPosition, caughtUp.AllStreamPosition!.Value.CommitPosition);
			Assert.AreEqual((ulong)lastEvent.EventPosition.Value.PreparePosition, caughtUp.AllStreamPosition.Value.PreparePosition);
			Assert.Null(caughtUp.StreamPosition);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class subscribe_all_from_end<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
	{
		protected override void Given()
		{
			EnableReadAll();
			WriteEvent("test-stream", "type1", "{}", "{Data: 1}");
			WriteEvent("test-stream", "type2", "{}", "{Data: 2}");
			WriteEvent("test-stream", "type3", "{}", "{Data: 3}");
		}

		[Test]
		public async Task should_receive_live_caught_up_message_immediately()
		{
			await using var sub = CreateAllSubscription(_publisher, Position.End);

			Assert.True(await sub.GetNext() is SubscriptionConfirmation);
			Assert.True(await sub.GetNext() is CaughtUp);
		}

		[Test]
		public async Task cancellation_preserves_the_callers_token()
		{
			using var cancellation = new CancellationTokenSource();
			await using var sub = CreateAllSubscription(_publisher, Position.End, cancellationToken: cancellation.Token);

			Assert.That(await sub.GetNext(), Is.InstanceOf<SubscriptionConfirmation>());
			Assert.That(await sub.GetNext(), Is.InstanceOf<CaughtUp>());
			cancellation.Cancel();

			var exception = Assert.ThrowsAsync<OperationCanceledException>(() => sub.GetNext());
			Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class subscribe_all_from_position<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
	{
		private readonly List<Guid> _eventIds = new();
		private TFPos _subscribeFrom;

		protected override void Given()
		{
			EnableReadAll();
			WriteEvent("test-stream", "type1", "{}", "{Data: 1}");
			WriteEvent("test-stream", "type2", "{}", "{Data: 2}");
			(_, _subscribeFrom) = WriteEvent("test-stream", "type3", "{}", "{Data: 3}");
			_eventIds.Add(WriteEvent("test-stream", "type4", "{}", "{Data: 4}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream", "type5", "{}", "{Data: 5}").Item1.EventId);
			_eventIds.Add(WriteEvent("test-stream", "type6", "{}", "{Data: 6}").Item1.EventId);
		}

		[Test]
		public async Task should_receive_events_after_start_position()
		{
			await using var sub = CreateAllSubscription(
				_publisher,
				new Position((ulong)_subscribeFrom.CommitPosition, (ulong)_subscribeFrom.PreparePosition));

			Assert.True(await sub.GetNext() is SubscriptionConfirmation);
			Assert.AreEqual(_eventIds[0], ((Event)await sub.GetNext()).Id);
			Assert.AreEqual(_eventIds[1], ((Event)await sub.GetNext()).Id);
			Assert.AreEqual(_eventIds[2], ((Event)await sub.GetNext()).Id);
			Assert.True(await sub.GetNext() is CaughtUp);
		}
	}
}
