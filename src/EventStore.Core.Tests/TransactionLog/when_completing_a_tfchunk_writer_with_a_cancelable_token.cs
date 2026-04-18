using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Transforms.Identity;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Plugins.Transforms;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_completing_a_tfchunk_writer_with_a_cancelable_token : SpecificationWithDirectory
{
	private TFChunkDb _db;
	private TFChunkWriter _writer;
	private TFChunk _chunk;

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();

		_db = new TFChunkDb(TFChunkHelper.CreateDbConfig(
			PathName,
			new FileCheckpoint(GetFilePathFor("writer.chk"), "writer"),
			new FileCheckpoint(GetFilePathFor("chaser.chk"), "chaser"),
			chunkSize: 4096));
		_writer = new TFChunkWriter(_db);
	}

	[TearDown]
	public override async Task TearDown()
	{
		_chunk?.Dispose();
		_chunk = null;
		_db?.Config.WriterCheckpoint.Close(flush: false);
		_db?.Config.ChaserCheckpoint.Close(flush: false);
		await base.TearDown();
	}

	[Test]
	public async Task complete_chunk_flushes_the_writer_checkpoint_even_if_cancellation_arrives_after_completion()
	{
		using var cancellationTokenSource = new CancellationTokenSource();
		_chunk = await TFChunk.CreateNew(GetFilePathFor("chunk-000000.000000"), 4096, 0, 0,
			isScavenged: false, inMem: false, unbuffered: false,
			writethrough: false, reduceFileCachePressure: false, tracker: new TFChunkTracker.NoOp(),
			transformFactory: new CancelDuringCompletionTransformFactory(cancellationTokenSource),
			token: CancellationToken.None);
		SetCurrentChunk(_writer, _chunk);

		Assert.DoesNotThrowAsync(async () => await _writer.CompleteChunk(cancellationTokenSource.Token));
		Assert.That(_chunk.IsReadOnly, Is.True);
		Assert.That(_db.Config.WriterCheckpoint.Read(), Is.EqualTo(_chunk.ChunkHeader.ChunkEndPosition));
	}

	private static void SetCurrentChunk(TFChunkWriter writer, TFChunk chunk)
	{
		typeof(TFChunkWriter)
			.GetField("_currentChunk", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(writer, chunk);
	}

	private sealed class CancelDuringCompletionTransformFactory(CancellationTokenSource cancellationTokenSource)
		: IChunkTransformFactory
	{
		public TransformType Type => TransformType.Identity;
		public int TransformDataPosition(int dataPosition) => dataPosition;
		public void CreateTransformHeader(Span<byte> transformHeader) => transformHeader.Clear();

		public ValueTask ReadTransformHeader(Stream stream, Memory<byte> transformHeader, CancellationToken token)
			=> token.IsCancellationRequested ? ValueTask.FromCanceled(token) : ValueTask.CompletedTask;

		public IChunkTransform CreateTransform(ReadOnlySpan<byte> transformHeader) =>
			new CancelDuringCompletionTransform(cancellationTokenSource);

		public int TransformHeaderLength => 0;
	}

	private sealed class CancelDuringCompletionTransform(CancellationTokenSource cancellationTokenSource)
		: IChunkTransform
	{
		public IChunkReadTransform Read => IdentityChunkReadTransform.Instance;
		public IChunkWriteTransform Write { get; } = new CancelDuringCompletionWriteTransform(cancellationTokenSource);
	}

	private sealed class CancelDuringCompletionWriteTransform(CancellationTokenSource cancellationTokenSource)
		: IChunkWriteTransform
	{
		private readonly IdentityChunkWriteTransform _inner = new();

		public ChunkDataWriteStream TransformData(ChunkDataWriteStream dataStream) => _inner.TransformData(dataStream);

		public ValueTask CompleteData(int footerSize, int alignmentSize, CancellationToken token) =>
			_inner.CompleteData(footerSize, alignmentSize, token);

		public ValueTask<int> WriteFooter(ReadOnlyMemory<byte> footer, CancellationToken token)
		{
			cancellationTokenSource.Cancel();
			return _inner.WriteFooter(footer, token);
		}
	}
}
