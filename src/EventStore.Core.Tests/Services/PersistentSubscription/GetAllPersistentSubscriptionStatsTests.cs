using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Metrics;
using EventStore.Core.Services;
using EventStore.Core.Services.PersistentSubscription;
using EventStore.Core.Services.PersistentSubscription.ConsumerStrategy;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;
using ReadStreamResult = EventStore.Core.Data.ReadStreamResult;
using ResolvedEvent = EventStore.Core.Data.ResolvedEvent;
using StreamMetadata = EventStore.Core.Data.StreamMetadata;

namespace EventStore.Core.Tests.Services.PersistentSubscription;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class GetAllPersistentSubscriptionStatsTests<TLogFormat, TStreamId> {
	private PersistentSubscriptionService<TStreamId> _sut;
	private FakeStorage _storage;

	[SetUp]
	public void SetUp() {
		var bus = new SynchronousScheduler();
		var ioDispatcher = new IODispatcher(bus, bus);
		var trackers = new Trackers();

		_storage = new FakeStorage();

		bus.Subscribe<ClientMessage.ReadStreamEventsBackward>(_storage);
		bus.Subscribe<ClientMessage.WriteEvents>(_storage);
		bus.Subscribe<ClientMessage.ReadStreamEventsBackwardCompleted>(ioDispatcher.BackwardReader);
		bus.Subscribe<ClientMessage.WriteEventsCompleted>(ioDispatcher.Writer);

		_sut = new PersistentSubscriptionService<TStreamId>(
			new QueuedHandlerThreadPool(bus, "test", new QueueStatsManager(), new QueueTrackers()),
			new FakeReadIndex<TLogFormat, TStreamId>(_ => false, new MetaStreamLookup()),
			ioDispatcher, bus,
			new PersistentSubscriptionConsumerStrategyRegistry(bus, bus,
				Array.Empty<IPersistentSubscriptionConsumerStrategyFactory>()),
			trackers.PersistentSubscriptionTracker);
	}

	[Test]
	public async Task returns_not_ready_with_zero_total_before_starting() {
		var response = await GetStats(offset: 0, count: 5);

		Assert.AreEqual(
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.NotReady,
			response.Result);
		Assert.That(response.Total, Is.Zero);
		Assert.That(response.SubscriptionStats, Is.Null);
	}

	[Test]
	public async Task returns_empty_page_when_no_subscriptions_exist() {
		StartAsLeader();

		var response = await GetStats(offset: 0, count: 5);

		Assert.AreEqual(
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success,
			response.Result);
		Assert.That(response.Total, Is.Zero);
		Assert.That(response.RequestedOffset, Is.EqualTo(0));
		Assert.That(response.RequestedCount, Is.EqualTo(5));
		Assert.That(response.SubscriptionStats, Is.Empty);
	}

	[Test]
	public async Task pages_by_subscription_in_stream_then_group_order() {
		StartAsLeader();
		await CreateSubscription("beta", "group-1");
		await CreateSubscription("alpha", "group-2");
		await CreateSubscription("alpha", "group-1");
		await CreateSubscription("gamma", "group-1");

		var response = await GetStats(offset: 1, count: 1);

		Assert.AreEqual(
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success,
			response.Result);
		Assert.That(response.Total, Is.EqualTo(4));
		Assert.That(response.RequestedOffset, Is.EqualTo(1));
		Assert.That(response.RequestedCount, Is.EqualTo(1));
		Assert.That(response.SubscriptionStats, Has.Count.EqualTo(1));
		Assert.That(response.SubscriptionStats[0].EventSource, Is.EqualTo("alpha"));
		Assert.That(response.SubscriptionStats[0].GroupName, Is.EqualTo("group-2"));
	}

	[Test]
	public async Task counts_subscription_records_when_returning_total_for_a_page() {
		StartAsLeader();
		await CreateSubscription("alpha", "group-1");
		await CreateSubscription("alpha", "group-2");
		await CreateSubscription("beta", "group-1");

		var response = await GetStats(offset: 0, count: 1);

		Assert.AreEqual(
			MonitoringMessage.GetPersistentSubscriptionStatsCompleted.OperationStatus.Success,
			response.Result);
		Assert.That(response.Total, Is.EqualTo(3));
		Assert.That(response.SubscriptionStats, Has.Count.EqualTo(1));
		Assert.That(response.SubscriptionStats[0].EventSource, Is.EqualTo("alpha"));
		Assert.That(response.SubscriptionStats[0].GroupName, Is.EqualTo("group-1"));
	}

	private void StartAsLeader() {
		_sut.Start();
		_sut.Handle(new SystemMessage.BecomeLeader(Guid.NewGuid()));
	}

	private async Task CreateSubscription(string stream, string groupName) {
		var envelope = new TcsEnvelope<ClientMessage.CreatePersistentSubscriptionToStreamCompleted>();
		_sut.Handle(new ClientMessage.CreatePersistentSubscriptionToStream(
			internalCorrId: Guid.NewGuid(),
			correlationId: Guid.NewGuid(),
			envelope: envelope,
			eventStreamId: stream,
			groupName: groupName,
			resolveLinkTos: false,
			startFrom: 0,
			messageTimeoutMilliseconds: 10_000,
			recordStatistics: false,
			maxRetryCount: 10,
			bufferSize: 10,
			liveBufferSize: 10,
			readbatchSize: 5,
			checkPointAfterMilliseconds: 1_000,
			minCheckPointCount: 10,
			maxCheckPointCount: 100,
			maxSubscriberCount: 10,
			namedConsumerStrategy: SystemConsumerStrategies.RoundRobin,
			user: ClaimsPrincipal.Current));

		var response = await envelope.Task.WithTimeout();
		Assert.AreEqual(
			ClientMessage.CreatePersistentSubscriptionToStreamCompleted.CreatePersistentSubscriptionToStreamResult.Success,
			response.Result,
			response.Reason);
	}

	private async Task<MonitoringMessage.GetPersistentSubscriptionStatsCompleted> GetStats(int offset, int count) {
		var envelope = new TcsEnvelope<MonitoringMessage.GetPersistentSubscriptionStatsCompleted>();
		_sut.Handle(new MonitoringMessage.GetAllPersistentSubscriptionStats(envelope, offset, count));
		return await envelope.Task.WithTimeout();
	}

	private sealed class FakeStorage :
		IHandle<ClientMessage.ReadStreamEventsBackward>,
		IHandle<ClientMessage.WriteEvents> {
		private readonly Dictionary<string, List<Event>> _streams = new();

		public void Handle(ClientMessage.ReadStreamEventsBackward msg) {
			var result = ReadStreamResult.NoStream;
			var resolvedEvents = new List<ResolvedEvent>();

			if (_streams.TryGetValue(msg.EventStreamId, out var events)) {
				result = ReadStreamResult.Success;

				foreach (var ev in events) {
					var prepareLogRecord = new PrepareLogRecord(
						logPosition: 0,
						correlationId: Guid.NewGuid(),
						eventId: Guid.NewGuid(),
						transactionPosition: 0,
						transactionOffset: 0,
						eventStreamId: msg.EventStreamId,
						eventStreamIdSize: null,
						expectedVersion: 0,
						timeStamp: DateTime.UtcNow,
						flags: PrepareFlags.Data,
						eventType: ev.EventType,
						eventTypeSize: null,
						data: Array.Empty<byte>(),
						metadata: Array.Empty<byte>());

					var eventRecord = new EventRecord(0, prepareLogRecord, msg.EventStreamId, ev.EventType);
					resolvedEvents.Add(ResolvedEvent.ForUnresolvedEvent(eventRecord));
				}
			}

			msg.Envelope.ReplyWith(new ClientMessage.ReadStreamEventsBackwardCompleted(
				correlationId: msg.CorrelationId,
				eventStreamId: msg.EventStreamId,
				fromEventNumber: 0,
				maxCount: msg.MaxCount,
				result: result,
				events: resolvedEvents.ToArray(),
				streamMetadata: new StreamMetadata(),
				isCachePublic: false,
				error: null,
				nextEventNumber: -1,
				lastEventNumber: 0,
				isEndOfStream: true,
				tfLastCommitPosition: 0));
		}

		public void Handle(ClientMessage.WriteEvents msg) {
			if (!_streams.TryGetValue(msg.EventStreamId, out var events)) {
				events = new List<Event>();
				_streams.Add(msg.EventStreamId, events);
			}

			events.AddRange(msg.Events);

			msg.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(
				correlationId: msg.CorrelationId,
				firstEventNumber: 0,
				lastEventNumber: 0,
				preparePosition: 0,
				commitPosition: 0));
		}
	}

	private sealed class MetaStreamLookup : IMetastreamLookup<TStreamId> {
		public bool IsMetaStream(TStreamId streamId) => throw new NotSupportedException();
		public TStreamId MetaStreamOf(TStreamId streamId) => throw new NotSupportedException();
		public TStreamId OriginalStreamOf(TStreamId streamId) => throw new NotSupportedException();
	}
}
