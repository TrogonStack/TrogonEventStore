using System;
using System.Collections.Generic;
using System.Linq;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Services.AwakeReaderService;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Tests;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Services.Processing;
using EventStore.Projections.Core.Services.Processing.MultiStream;
using EventStore.Projections.Core.Tests.Services.core_projection;
using NUnit.Framework;
using ResolvedEvent = EventStore.Core.Data.ResolvedEvent;

namespace EventStore.Projections.Core.Tests.Services.event_reader.multi_stream_reader;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_handling_read_completed_for_all_streams_and_eofs<TLogFormat, TStreamId> : TestFixtureWithExistingEvents<TLogFormat, TStreamId>
{
	private MultiStreamEventReader _edp;
	private Guid _distibutionPointCorrelationId;
	private Guid _firstEventId;
	private Guid _secondEventId;
	private Guid _thirdEventId;
	private Guid _fourthEventId;

	protected override void Given()
	{
		TicksAreHandledImmediately();
	}

	private string[] _abStreams;
	private Dictionary<string, long> _ab12Tag;

	[SetUp]
	public new void When()
	{
		_ab12Tag = new Dictionary<string, long> { { "a", 1 }, { "b", 2 } };
		_abStreams = new[] { "a", "b" };

		_distibutionPointCorrelationId = Guid.NewGuid();
		_edp = new MultiStreamEventReader(
			_ioDispatcher, _bus, _distibutionPointCorrelationId, null, 0, _abStreams, _ab12Tag, false,
			new RealTimeProvider());
		_edp.Resume();
		_firstEventId = Guid.NewGuid();
		_secondEventId = Guid.NewGuid();
		_thirdEventId = Guid.NewGuid();
		_fourthEventId = Guid.NewGuid();
		var correlationId = _consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
			.Last(x => x.EventStreamId == "a").CorrelationId;
		_edp.Handle(
			new ClientMessage.ReadStreamEventsForwardCompleted(
				correlationId, "a", 100, 100, ReadStreamResult.Success,
				new[] {
					ResolvedEvent.ForUnresolvedEvent(
						new EventRecord(
							1, 50, Guid.NewGuid(), _firstEventId, 50, 0, "a", ExpectedVersion.Any, DateTime.UtcNow,
							PrepareFlags.SingleWrite | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
							"event_type1", new byte[] {1}, new byte[] {2})),
					ResolvedEvent.ForUnresolvedEvent(
						new EventRecord(
							2, 150, Guid.NewGuid(), _secondEventId, 150, 0, "a", ExpectedVersion.Any,
							DateTime.UtcNow,
							PrepareFlags.SingleWrite | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
							"event_type2", new byte[] {3}, new byte[] {4}))
				}, null, false, "", 3, 2, true, 200));
		correlationId = _consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
			.Last(x => x.EventStreamId == "a").CorrelationId;
		_edp.Handle(
			new ClientMessage.ReadStreamEventsForwardCompleted(
				correlationId, "b", 100, 100, ReadStreamResult.Success,
				new[] {
					ResolvedEvent.ForUnresolvedEvent(
						new EventRecord(
							2, 100, Guid.NewGuid(), _thirdEventId, 100, 0, "b", ExpectedVersion.Any,
							DateTime.UtcNow,
							PrepareFlags.SingleWrite | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
							"event_type1", new byte[] {1}, new byte[] {2})),
					ResolvedEvent.ForUnresolvedEvent(
						new EventRecord(
							3, 200, Guid.NewGuid(), _fourthEventId, 200, 0, "b", ExpectedVersion.Any,
							DateTime.UtcNow,
							PrepareFlags.SingleWrite | PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd,
							"event_type2", new byte[] {3}, new byte[] {4}))
				}, null, false, "", 4, 3, true, 200));
		correlationId = _consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
			.Last(x => x.EventStreamId == "a").CorrelationId;
		_edp.Handle(
			new ClientMessage.ReadStreamEventsForwardCompleted(
				correlationId, "a", 100, 100, ReadStreamResult.Success, new ResolvedEvent[0], null, false, "", 3,
				2, true, 400));
		correlationId = _consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
			.Last(x => x.EventStreamId == "b").CorrelationId;
		_edp.Handle(
			new ClientMessage.ReadStreamEventsForwardCompleted(
				correlationId, "b", 100, 100, ReadStreamResult.Success, new ResolvedEvent[0], null, false, "", 4,
				3, true, 400));
	}

	[Test]
	public void publishes_correct_committed_event_received_messages()
	{
		Assert.AreEqual(
			6, _consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>().Count());
		var first =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>().First();
		var fifth =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(4)
				.First();
		var sixth =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(5)
				.First();

		Assert.AreEqual("event_type1", first.Data.EventType);
		Assert.IsNull(fifth.Data);
		Assert.IsNull(sixth.Data);
		Assert.AreEqual(_firstEventId, first.Data.EventId);
		Assert.AreEqual(1, first.Data.Data[0]);
		Assert.AreEqual(2, first.Data.Metadata[0]);
		Assert.AreEqual("a", first.Data.EventStreamId);
		Assert.IsNull(fifth.Data);
		//            Assert.IsNullOrEmpty("", fifth.EventStreamId);
		Assert.AreEqual(50, first.Data.Position.PreparePosition);
		//            Assert.AreEqual(0, fifth.Position.PreparePosition);
		Assert.AreEqual(-1, first.Data.Position.CommitPosition);
		//            Assert.AreEqual(0, fifth.Position.CommitPosition);
		Assert.AreEqual(50, first.SafeTransactionFileReaderJoinPosition);
		//            Assert.AreEqual(200, fifth.SafeTransactionFileReaderJoinPosition);
		Assert.AreEqual(400, sixth.SafeTransactionFileReaderJoinPosition);
	}

	[Test]
	public void publishes_read_events_from_beginning_with_correct_next_event_number()
	{
		Assert.AreEqual(4, _consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>().Count());
		Assert.IsTrue(
			_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
				.Any(m => m.EventStreamId == "a"));
		Assert.IsTrue(
			_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
				.Any(m => m.EventStreamId == "b"));
		Assert.AreEqual(
			3,
			_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
				.Last(m => m.EventStreamId == "a")
				.FromEventNumber);
		Assert.AreEqual(
			4,
			_consumer.HandledMessages.OfType<ClientMessage.ReadStreamEventsForward>()
				.Last(m => m.EventStreamId == "b")
				.FromEventNumber);
	}

	[Test]
	public void publishes_subscribe_awake()
	{
		Assert.AreEqual(4, _consumer.HandledMessages.OfType<AwakeServiceMessage.SubscribeAwake>().Count());
	}


	[Test]
	public void publishes_committed_event_received_messages_in_correct_order()
	{
		Assert.AreEqual(
			6, _consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>().Count());
		var first =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(0)
				.First();
		var second =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(1)
				.First();
		var third =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(2)
				.First();
		var fourth =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(3)
				.First();
		var fifth =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(4)
				.First();
		var sixth =
			_consumer.HandledMessages.OfType<ReaderSubscriptionMessage.CommittedEventDistributed>()
				.Skip(5)
				.First();

		Assert.AreEqual(first.Data.EventId, _firstEventId);
		Assert.AreEqual(second.Data.EventId, _thirdEventId);
		Assert.AreEqual(third.Data.EventId, _secondEventId);
		Assert.AreEqual(fourth.Data.EventId, _fourthEventId);
		Assert.IsNull(fifth.Data);
		Assert.IsNull(sixth.Data);
	}
}
