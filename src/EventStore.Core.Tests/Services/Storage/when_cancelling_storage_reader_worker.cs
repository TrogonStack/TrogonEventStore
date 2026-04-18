using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage;
using EventStore.Core.Services.Storage.InMemory;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.Fakes;
using EventStore.Core.TransactionLog.Checkpoint;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage;

[TestFixture]
public class when_cancelling_storage_reader_worker
{
	[Test]
	public async Task read_event_cancellation_from_message_token_is_rethrown_without_reply()
	{
		var readIndex = new BlockingReadIndex();
		var worker = CreateWorker(readIndex);
		var reply = default(Message);
		using var messageCancellation = new CancellationTokenSource();
		var message = new ClientMessage.ReadEvent(
			Guid.NewGuid(),
			Guid.NewGuid(),
			new CallbackEnvelope(m => reply = m),
			"stream",
			0,
			resolveLinkTos: false,
			requireLeader: false,
			user: new ClaimsPrincipal(),
			cancellationToken: messageCancellation.Token);

		var task = ((IAsyncHandle<ClientMessage.ReadEvent>)worker)
			.HandleAsync(message, CancellationToken.None)
			.AsTask();

		Assert.That(readIndex.ReadEventStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);

		messageCancellation.Cancel();

		var ex = Assert.CatchAsync<OperationCanceledException>(async () => await task);
		Assert.That(ex?.CancellationToken, Is.EqualTo(messageCancellation.Token));
		Assert.That(reply, Is.Null);
	}

	[Test]
	public async Task effective_stream_acl_cancellation_from_message_token_replies_with_operation_cancelled()
	{
		var readIndex = new BlockingReadIndex();
		var worker = CreateWorker(readIndex);
		var reply = default(Message);
		using var messageCancellation = new CancellationTokenSource();
		var message = new StorageMessage.EffectiveStreamAclRequest(
			"stream",
			new CallbackEnvelope(m => reply = m),
			messageCancellation.Token);

		var task = ((IAsyncHandle<StorageMessage.EffectiveStreamAclRequest>)worker)
			.HandleAsync(message, CancellationToken.None)
			.AsTask();

		Assert.That(readIndex.EffectiveAclStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);

		messageCancellation.Cancel();
		await task;

		Assert.That(reply, Is.TypeOf<StorageMessage.OperationCancelledMessage>());
		Assert.That(reply?.CancellationToken, Is.EqualTo(messageCancellation.Token));
	}

	[Test]
	public async Task effective_stream_acl_cancellation_from_queue_token_is_rethrown_without_reply()
	{
		var readIndex = new BlockingReadIndex();
		var worker = CreateWorker(readIndex);
		var reply = default(Message);
		using var queueCancellation = new CancellationTokenSource();
		using var messageCancellation = new CancellationTokenSource();
		var message = new StorageMessage.EffectiveStreamAclRequest(
			"stream",
			new CallbackEnvelope(m => reply = m),
			messageCancellation.Token);

		var task = ((IAsyncHandle<StorageMessage.EffectiveStreamAclRequest>)worker)
			.HandleAsync(message, queueCancellation.Token)
			.AsTask();

		Assert.That(readIndex.EffectiveAclStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);

		queueCancellation.Cancel();

		var ex = Assert.CatchAsync<OperationCanceledException>(async () => await task);
		Assert.That(ex?.CancellationToken, Is.EqualTo(queueCancellation.Token));
		Assert.That(reply, Is.Null);
	}

	private static StorageReaderWorker<string> CreateWorker(BlockingReadIndex readIndex) =>
		new(
			new NoopPublisher(),
			readIndex,
			new StubSystemStreamLookup(),
			new StubCheckpoint(),
			new StubInMemoryStreamReader(),
			queueId: 0);

	private sealed class BlockingReadIndex : IReadIndex<string>
	{
		public ManualResetEventSlim ReadEventStarted { get; } = new(false);
		public ManualResetEventSlim EffectiveAclStarted { get; } = new(false);

		public long LastIndexedPosition => 0;
		public IIndexWriter<string> IndexWriter => throw new NotSupportedException();

		public ValueTask<IndexReadEventResult> ReadEvent(string streamName, string streamId, long eventNumber,
			CancellationToken token) =>
			AwaitCancellation<IndexReadEventResult>(ReadEventStarted, token);

		public ValueTask<StorageMessage.EffectiveAcl> GetEffectiveAcl(string streamId, CancellationToken token) =>
			AwaitCancellation<StorageMessage.EffectiveAcl>(EffectiveAclStarted, token);

		public string GetStreamId(string streamName) => streamName;

		public ReadIndexStats GetStatistics() => throw new NotSupportedException();
		public ValueTask<IndexReadStreamResult> ReadStreamEventsBackward(string streamName, string streamId,
			long fromEventNumber, int maxCount, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadStreamResult> ReadStreamEventsForward(string streamName, string streamId,
			long fromEventNumber, int maxCount, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadEventInfoResult> ReadEventInfo_KeepDuplicates(string streamId, long eventNumber,
			CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadEventInfoResult> ReadEventInfoForward_KnownCollisions(string streamId,
			long fromEventNumber, int maxCount, long beforePosition, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadEventInfoResult> ReadEventInfoForward_NoCollisions(ulong stream,
			long fromEventNumber, int maxCount, long beforePosition, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadEventInfoResult> ReadEventInfoBackward_KnownCollisions(string streamId,
			long fromEventNumber, int maxCount, long beforePosition, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadEventInfoResult> ReadEventInfoBackward_NoCollisions(ulong stream,
			Func<ulong, string> getStreamId, long fromEventNumber, int maxCount, long beforePosition,
			CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<bool> IsStreamDeleted(string streamId, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<long> GetStreamLastEventNumber(string streamId, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<long> GetStreamLastEventNumber_KnownCollisions(string streamId, long beforePosition,
			CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<long> GetStreamLastEventNumber_NoCollisions(ulong stream, Func<ulong, string> getStreamId,
			long beforePosition, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<StreamMetadata> GetStreamMetadata(string streamId, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<string> GetEventStreamIdByTransactionId(long transactionId, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<string> GetStreamName(string streamId, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadAllResult> ReadAllEventsForward(TFPos pos, int maxCount, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadAllResult> ReadAllEventsBackward(TFPos pos, int maxCount, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadAllResult> ReadAllEventsForwardFiltered(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter, CancellationToken token) =>
			throw new NotSupportedException();
		public ValueTask<IndexReadAllResult> ReadAllEventsBackwardFiltered(TFPos pos, int maxCount, int maxSearchWindow,
			IEventFilter eventFilter, CancellationToken token) =>
			throw new NotSupportedException();
		public void Close() => throw new NotSupportedException();
		public void Dispose()
		{
			ReadEventStarted.Dispose();
			EffectiveAclStarted.Dispose();
		}

		private static async ValueTask<T> AwaitCancellation<T>(ManualResetEventSlim started, CancellationToken token)
		{
			started.Set();
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			throw new InvalidOperationException("Expected cancellation before completion.");
		}
	}

	private sealed class StubSystemStreamLookup : ISystemStreamLookup<string>
	{
		public string AllStream => "$all";
		public string SettingsStream => "$settings";

		public bool IsMetaStream(string streamId) => false;
		public string MetaStreamOf(string streamId) => "$$" + streamId;
		public string OriginalStreamOf(string streamId) => streamId;
		public ValueTask<bool> IsSystemStream(string streamId, CancellationToken token) => new(false);
	}

	private sealed class StubCheckpoint : IReadOnlyCheckpoint
	{
		public string Name => "stub";
		public event Action<long> Flushed
		{
			add { }
			remove { }
		}

		public long Read() => 0;
		public long ReadNonFlushed() => 0;
	}

	private sealed class StubInMemoryStreamReader : IInMemoryStreamReader
	{
		public ClientMessage.ReadStreamEventsForwardCompleted ReadForwards(ClientMessage.ReadStreamEventsForward msg) =>
			throw new NotSupportedException();

		public ClientMessage.ReadStreamEventsBackwardCompleted ReadBackwards(ClientMessage.ReadStreamEventsBackward msg) =>
			throw new NotSupportedException();
	}
}
