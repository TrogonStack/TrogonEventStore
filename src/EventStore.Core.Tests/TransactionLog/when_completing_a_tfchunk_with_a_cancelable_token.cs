using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_completing_a_tfchunk_with_a_cancelable_token : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;
	private ObservingChunkHandle _observingHandle;
	private long _nextLogPosition;

	[SetUp]
	public async Task SetUp()
	{
		Filename = Path.Combine(Path.GetTempPath(), $"{nameof(when_completing_a_tfchunk_with_a_cancelable_token)}-{Guid.NewGuid()}");

		_chunk = await TFChunkHelper.CreateNewChunk(Filename);
		var record = LogRecord.Commit(0, Guid.NewGuid(), 0, 0);
		var writeResult = await _chunk.TryAppend(record, CancellationToken.None);
		Assert.That(writeResult.Success, Is.True);
		_nextLogPosition = writeResult.NewPosition;

		var handleField = typeof(TFChunk)
			.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
		var originalHandle = (IChunkHandle)handleField.GetValue(_chunk)!;
		_observingHandle = new ObservingChunkHandle(originalHandle);
		handleField.SetValue(_chunk, _observingHandle);
	}

	[TearDown]
	public void TearDown()
	{
		_chunk?.Dispose();
		_chunk = null;
		if (File.Exists(Filename))
			File.Delete(Filename);
	}

	[Test]
	public async Task appending_does_not_forward_the_cancelable_token_to_writes()
	{
		using var cancellationTokenSource = new CancellationTokenSource();
		var record = LogRecord.Commit(_nextLogPosition, Guid.NewGuid(), _nextLogPosition, 0);

		var writeResult = await _chunk.TryAppend(record, cancellationTokenSource.Token);

		Assert.That(writeResult.Success, Is.True);
		Assert.That(_observingHandle.SawCancelableWriteToken, Is.False);
	}

	[Test]
	public async Task completes_without_forwarding_the_cancelable_token_to_writes_or_read_only_transition()
	{
		using var cancellationTokenSource = new CancellationTokenSource();

		await _chunk.Complete(cancellationTokenSource.Token);

		Assert.That(_chunk.IsReadOnly, Is.True);
		Assert.That(_observingHandle.SetReadOnlyCalls, Is.EqualTo(1));
		Assert.That(_observingHandle.SawCancelableWriteToken, Is.False);
		Assert.That(_observingHandle.SawCancelableReadOnlyToken, Is.False);
	}

	private sealed class ObservingChunkHandle(IChunkHandle inner) : IChunkHandle
	{
		public int SetReadOnlyCalls { get; private set; }
		public bool SawCancelableWriteToken { get; private set; }
		public bool SawCancelableReadOnlyToken { get; private set; }

		public long Length
		{
			get => inner.Length;
			set => inner.Length = value;
		}

		public FileAccess Access => inner.Access;

		public void Flush() => inner.Flush();

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token)
		{
			SawCancelableWriteToken |= token.CanBeCanceled;
			return inner.WriteAsync(data, offset, token);
		}

		public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token) =>
			inner.ReadAsync(buffer, offset, token);

		public ValueTask SetReadOnlyAsync(bool value, CancellationToken token)
		{
			SetReadOnlyCalls++;
			SawCancelableReadOnlyToken |= token.CanBeCanceled;
			return inner.SetReadOnlyAsync(value, token);
		}

		public Stream CreateStream() => inner.CreateStream();

		public void Dispose() => inner.Dispose();
	}
}
