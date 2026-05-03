using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Metrics;
using EventStore.Core.Services;
using EventStore.Core.Services.PersistentSubscription;
using EventStore.Core.Services.PersistentSubscription.ConsumerStrategy;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.Services.Replication;
using EventStore.Core.Tests.TransactionLog;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.PersistentSubscription;

[TestFixture]
public class PersistentSubscriptionServiceNotReadyTests {
	private PersistentSubscriptionService<string> _sut;

	[SetUp]
	public void SetUp() {
		var bus = new SynchronousScheduler();
		var trackers = new Trackers();

		_sut = new PersistentSubscriptionService<string>(
			new QueuedHandlerThreadPool(bus, "test", new QueueStatsManager(), new QueueTrackers()),
			new FakeReadIndex<LogFormat.V2, string>(_ => false, new MetaStreamLookup()),
			new IODispatcher(bus, bus), bus,
			new PersistentSubscriptionConsumerStrategyRegistry(
				bus,
				bus,
				Array.Empty<IPersistentSubscriptionConsumerStrategyFactory>()),
			trackers.PersistentSubscriptionTracker);
	}

	[Test]
	public void create_stream_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.CreatePersistentSubscriptionToStream(
			Guid.NewGuid(), correlationId, envelope, "stream", "group", false, 0L, 10_000, false, 10, 10, 10, 5,
			1_000, 10, 100, 10, SystemConsumerStrategies.RoundRobin, ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public void create_all_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.CreatePersistentSubscriptionToAll(
			Guid.NewGuid(), correlationId, envelope, "group", EventFilter.DefaultAllFilter, false, new TFPos(0, 0),
			10_000, false, 10, 10, 10, 5, 1_000, 10, 100, 10, SystemConsumerStrategies.RoundRobin,
			ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public void update_stream_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.UpdatePersistentSubscriptionToStream(
			Guid.NewGuid(), correlationId, envelope, "stream", "group", false, 0L, 10_000, false, 10, 10, 10, 5,
			1_000, 10, 100, 10, SystemConsumerStrategies.RoundRobin, ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public void update_all_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.UpdatePersistentSubscriptionToAll(
			Guid.NewGuid(), correlationId, envelope, "group", false, new TFPos(0, 0), 10_000, false, 10, 10, 10, 5,
			1_000, 10, 100, 10, SystemConsumerStrategies.RoundRobin, ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public void delete_stream_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.DeletePersistentSubscriptionToStream(
			Guid.NewGuid(), correlationId, envelope, "stream", "group", ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public void delete_all_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.DeletePersistentSubscriptionToAll(
			Guid.NewGuid(), correlationId, envelope, "group", ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public async Task connect_stream_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		await ((IAsyncHandle<ClientMessage.ConnectToPersistentSubscriptionToStream>)_sut).HandleAsync(
			new ClientMessage.ConnectToPersistentSubscriptionToStream(
				Guid.NewGuid(), correlationId, envelope, Guid.NewGuid(), "connection", "group", "stream", 1,
				"source", ClaimsPrincipal.Current),
			CancellationToken.None);

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public async Task connect_all_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		await ((IAsyncHandle<ClientMessage.ConnectToPersistentSubscriptionToAll>)_sut).HandleAsync(
			new ClientMessage.ConnectToPersistentSubscriptionToAll(
				Guid.NewGuid(), correlationId, envelope, Guid.NewGuid(), "connection", "group", 1, "source",
				ClaimsPrincipal.Current),
			CancellationToken.None);

		AssertNotReady(envelope, correlationId);
	}

	[Test]
	public void read_next_replies_not_ready() {
		var envelope = new FakeEnvelope();
		var correlationId = Guid.NewGuid();

		_sut.Handle(new ClientMessage.ReadNextNPersistentMessages(
			Guid.NewGuid(), correlationId, envelope, "stream", "group", 10, ClaimsPrincipal.Current));

		AssertNotReady(envelope, correlationId);
	}

	private static void AssertNotReady(FakeEnvelope envelope, Guid correlationId) {
		Assert.That(envelope.Replies, Has.Count.EqualTo(1));
		var reply = envelope.Replies.Single();

		Assert.That(reply, Is.TypeOf<ClientMessage.NotHandled>());
		var notHandled = (ClientMessage.NotHandled)reply;
		Assert.That(notHandled.CorrelationId, Is.EqualTo(correlationId));
		Assert.That(notHandled.Reason, Is.EqualTo(ClientMessage.NotHandled.Types.NotHandledReason.NotReady));
	}

	private sealed class MetaStreamLookup : IMetastreamLookup<string> {
		public bool IsMetaStream(string streamId) => throw new NotSupportedException();

		public string MetaStreamOf(string streamId) => throw new NotSupportedException();

		public string OriginalStreamOf(string streamId) => throw new NotSupportedException();
	}
}
