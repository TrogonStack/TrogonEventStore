using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
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
	private static EnumeratorWrapper CreateStreamSubscription<TStreamId>(
		IPublisher publisher,
		string streamName,
		StreamRevision? checkpoint = null,
		ClaimsPrincipal user = null,
		CancellationToken cancellationToken = default)
	{

		return new EnumeratorWrapper(new Enumerator.StreamSubscription<TStreamId>(
			bus: publisher,
			expiryStrategy: new DefaultExpiryStrategy(),
			streamName: streamName,
			checkpoint: checkpoint,
			resolveLinks: false,
			user: user ?? SystemAccounts.System,
			requiresLeader: false,
			cancellationToken: cancellationToken));
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class subscribe_stream_from_start_<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
	{
		private readonly List<Guid> _eventIds = new();

		protected override void Given()
		{
			EnableReadAll();
			_eventIds.Add(WriteEvent("test-stream1", "type1", "{}", "{Data: 1}").Item1.EventId);
			WriteEvent("test-stream2", "type2", "{}", "{Data: 2}");
			WriteEvent("test-stream3", "type3", "{}", "{Data: 3}");
		}

		[Test]
		public async Task should_receive_live_caught_up_message_after_reading_existing_events()
		{
			await using var sub = CreateStreamSubscription<TStreamId>(
				_publisher, streamName: "test-stream1");

			Assert.True(await sub.GetNext() is SubscriptionConfirmation);
			Assert.AreEqual(_eventIds[0], ((Event)await sub.GetNext()).Id);

			var caughtUp = (CaughtUp)await sub.GetNext();
			Assert.Null(caughtUp.AllStreamPosition);
			Assert.AreEqual(0, caughtUp.StreamPosition);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class subscribe_stream_from_end<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
	{
		private readonly List<Guid> _eventIds = new();

		protected override void Given()
		{
			EnableReadAll();
			_eventIds.Add(WriteEvent("test-stream1", "type1", "{}", "{Data: 1}").Item1.EventId);
			WriteEvent("test-stream2", "type2", "{}", "{Data: 2}");
			WriteEvent("test-stream3", "type3", "{}", "{Data: 3}");
		}

		[Test]
		public async Task should_receive_live_caught_up_message_immediately()
		{
			await using var enumerator = CreateStreamSubscription<TStreamId>(
				_publisher, streamName: "test-stream1", StreamRevision.End);

			Assert.True(await enumerator.GetNext() is SubscriptionConfirmation);
			Assert.True(await enumerator.GetNext() is CaughtUp);
		}

		[Test]
		public async Task cancellation_preserves_the_callers_token()
		{
			using var cancellation = new CancellationTokenSource();
			await using var enumerator = CreateStreamSubscription<TStreamId>(
				_publisher, streamName: "test-stream1", StreamRevision.End, cancellationToken: cancellation.Token);

			Assert.That(await enumerator.GetNext(), Is.InstanceOf<SubscriptionConfirmation>());
			Assert.That(await enumerator.GetNext(), Is.InstanceOf<CaughtUp>());
			cancellation.Cancel();

			var exception = Assert.ThrowsAsync<OperationCanceledException>(() => enumerator.GetNext());
			Assert.That(exception!.CancellationToken, Is.EqualTo(cancellation.Token));
		}
	}
}
