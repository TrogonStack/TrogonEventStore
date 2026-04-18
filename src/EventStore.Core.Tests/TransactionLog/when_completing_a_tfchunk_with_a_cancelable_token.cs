using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Transforms.Identity;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Plugins.Transforms;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_completing_a_tfchunk_with_a_cancelable_token : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;
	private ObservingChunkHandle _observingHandle;
	private ObservingWriteState _writeState;
	private long _nextLogPosition;

	[SetUp]
	public async Task SetUp()
	{
		Filename = Path.Combine(Path.GetTempPath(), $"{nameof(when_completing_a_tfchunk_with_a_cancelable_token)}-{Guid.NewGuid()}");

		_writeState = new ObservingWriteState();
		_chunk = await TFChunk.CreateNew(Filename, 4096, 0, 0,
			isScavenged: false, inMem: false, unbuffered: false,
			writethrough: false, reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			transformFactory: new ObservingChunkTransformFactory(_writeState),
			token: CancellationToken.None);
		var record = LogRecord.Commit(0, Guid.NewGuid(), 0, 0);
		var writeResult = await _chunk.TryAppend(record, CancellationToken.None);
		Assert.That(writeResult.Success, Is.True);
		_nextLogPosition = writeResult.NewPosition;

		var handleField = typeof(TFChunk)
			.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
		var originalHandle = (IChunkHandle)handleField.GetValue(_chunk)!;
		_observingHandle = new ObservingChunkHandle(originalHandle);
		handleField.SetValue(_chunk, _observingHandle);
		_writeState.Reset();
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
		Assert.That(_writeState.StreamWriteCalls, Is.GreaterThan(0));
		Assert.That(_writeState.SawCancelableStreamWriteToken, Is.False);
	}

	[Test]
	public async Task completes_without_forwarding_the_cancelable_token_to_writes_or_read_only_transition()
	{
		using var cancellationTokenSource = new CancellationTokenSource();

		await _chunk.Complete(cancellationTokenSource.Token);

		Assert.That(_chunk.IsReadOnly, Is.True);
		Assert.That(_writeState.CompleteDataCalls, Is.EqualTo(1));
		Assert.That(_writeState.FooterWriteCalls, Is.EqualTo(1));
		Assert.That(_writeState.SawCancelableCompleteDataToken, Is.False);
		Assert.That(_writeState.SawCancelableFooterToken, Is.False);
		Assert.That(_observingHandle.SetReadOnlyCalls, Is.EqualTo(1));
		Assert.That(_observingHandle.SawCancelableReadOnlyToken, Is.False);
	}

	private sealed class ObservingWriteState
	{
		public int StreamWriteCalls { get; private set; }
		public int CompleteDataCalls { get; private set; }
		public int FooterWriteCalls { get; private set; }
		public bool SawCancelableStreamWriteToken { get; private set; }
		public bool SawCancelableCompleteDataToken { get; private set; }
		public bool SawCancelableFooterToken { get; private set; }

		public void ObserveStreamWrite(CancellationToken token)
		{
			StreamWriteCalls++;
			SawCancelableStreamWriteToken |= token.CanBeCanceled;
		}

		public void ObserveCompleteData(CancellationToken token)
		{
			CompleteDataCalls++;
			SawCancelableCompleteDataToken |= token.CanBeCanceled;
		}

		public void ObserveFooterWrite(CancellationToken token)
		{
			FooterWriteCalls++;
			SawCancelableFooterToken |= token.CanBeCanceled;
		}

		public void Reset()
		{
			StreamWriteCalls = 0;
			CompleteDataCalls = 0;
			FooterWriteCalls = 0;
			SawCancelableStreamWriteToken = false;
			SawCancelableCompleteDataToken = false;
			SawCancelableFooterToken = false;
		}
	}

	private sealed class ObservingChunkTransformFactory(ObservingWriteState writeState) : IChunkTransformFactory
	{
		public TransformType Type => TransformType.Identity;
		public int TransformDataPosition(int dataPosition) => dataPosition;
		public void CreateTransformHeader(Span<byte> transformHeader) => transformHeader.Clear();

		public ValueTask ReadTransformHeader(Stream stream, Memory<byte> transformHeader, CancellationToken token)
			=> token.IsCancellationRequested ? ValueTask.FromCanceled(token) : ValueTask.CompletedTask;

		public IChunkTransform CreateTransform(ReadOnlySpan<byte> transformHeader) =>
			new ObservingChunkTransform(writeState);

		public int TransformHeaderLength => 0;
	}

	private sealed class ObservingChunkTransform(ObservingWriteState writeState) : IChunkTransform
	{
		public IChunkReadTransform Read => IdentityChunkReadTransform.Instance;
		public IChunkWriteTransform Write { get; } = new ObservingChunkWriteTransform(writeState);
	}

	private sealed class ObservingChunkWriteTransform(ObservingWriteState writeState) : IChunkWriteTransform
	{
		private ObservingChunkWriteStream _stream;

		public ChunkDataWriteStream TransformData(ChunkDataWriteStream dataStream)
		{
			_stream = new ObservingChunkWriteStream(dataStream, writeState);
			return _stream;
		}

		public ValueTask CompleteData(int footerSize, int alignmentSize, CancellationToken token)
		{
			writeState.ObserveCompleteData(token);
			var chunkHeaderAndDataSize = (int)_stream.Position;
			var alignedSize = GetAlignedSize(chunkHeaderAndDataSize + footerSize, alignmentSize);
			var paddingSize = alignedSize - chunkHeaderAndDataSize - footerSize;

			return paddingSize > 0
				? _stream.WriteAsync(new byte[paddingSize], token)
				: ValueTask.CompletedTask;
		}

		public async ValueTask<int> WriteFooter(ReadOnlyMemory<byte> footer, CancellationToken token)
		{
			writeState.ObserveFooterWrite(token);
			await _stream.ChunkFileStream.WriteAsync(footer, token);
			return (int)_stream.ChunkFileStream.Length;
		}

		private static int GetAlignedSize(int size, int alignmentSize)
		{
			if (size % alignmentSize == 0)
				return size;
			return (size / alignmentSize + 1) * alignmentSize;
		}
	}

	private sealed class ObservingChunkWriteStream(ChunkDataWriteStream stream, ObservingWriteState writeState) :
		ChunkDataWriteStream(stream.ChunkFileStream, stream.ChecksumAlgorithm)
	{
		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
		{
			writeState.ObserveStreamWrite(token);
			return base.WriteAsync(buffer, token);
		}
	}

	private sealed class ObservingChunkHandle(IChunkHandle inner) : IChunkHandle
	{
		public int SetReadOnlyCalls { get; private set; }
		public bool SawCancelableReadOnlyToken { get; private set; }

		public long Length
		{
			get => inner.Length;
			set => inner.Length = value;
		}

		public FileAccess Access => inner.Access;

		public void Flush() => inner.Flush();

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token) =>
			inner.WriteAsync(data, offset, token);

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
