using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using DotNext.Buffers;
using DotNext.IO;
using EventStore.Plugins.Transforms;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

internal sealed class WriterWorkItem : Disposable
{
	public const int BufferSize = 8192;

	public Stream WorkingStream { get; private set; }

	private readonly Stream _fileStream;
	private readonly IBufferedWriter _bufferedWriter;
	private Stream _memStream;
	public readonly IncrementalHash MD5;

	public unsafe WriterWorkItem(nint memoryPtr, int length, IncrementalHash md5,
		IChunkWriteTransform chunkWriteTransform, int initialStreamPosition)
	{
		var memStream =
			new UnmanagedMemoryStream((byte*)memoryPtr, length, length, FileAccess.ReadWrite)
			{
				Position = initialStreamPosition,
			};

		var chunkDataWriteStream = new ChunkDataWriteStream(memStream, md5);
		WorkingStream = _memStream = chunkWriteTransform.TransformData(chunkDataWriteStream);
		MD5 = md5;
	}

	public WriterWorkItem(IChunkHandle handle, IncrementalHash md5, bool unbuffered,
		IChunkWriteTransform chunkWriteTransform, int initialStreamPosition)
	{
		var chunkStream = handle.CreateStream();
		var fileStream = unbuffered
			? chunkStream
			: new PoolingBufferedStream(chunkStream, leaveOpen: false) { MaxBufferSize = BufferSize };
		fileStream.Position = initialStreamPosition;
		var chunkDataWriteStream = new ChunkDataWriteStream(fileStream, md5);

		WorkingStream = _fileStream = chunkWriteTransform.TransformData(chunkDataWriteStream);
		MD5 = md5;
		_bufferedWriter = WorkingStream.GetType() == typeof(ChunkDataWriteStream)
			? fileStream as IBufferedWriter
			: null;
	}

	public Memory<byte> TryGetDirectBuffer(int length)
	{
		if (_bufferedWriter is not PoolingBufferedStream { HasBufferedDataToRead: false })
		{
			return Memory<byte>.Empty;
		}

		var buffer = _bufferedWriter.Buffer;
		return buffer.Length >= length
			? buffer[..length]
			: Memory<byte>.Empty;
	}

	public void SetMemStream(UnmanagedMemoryStream memStream)
	{
		_memStream = memStream;
		if (_fileStream is null)
		{
			WorkingStream = memStream;
		}
	}

	public ValueTask AppendData(ReadOnlyMemory<byte> buf, CancellationToken _)
	{
		// MEMORY (in-memory write doesn't require async I/O)
		_memStream?.Write(buf.Span);

		// as we are always append-only, stream's position should be right here
		return _fileStream?.WriteAsync(buf, CancellationToken.None) ?? ValueTask.CompletedTask;
	}

	public void AppendData(int length)
	{
		Debug.Assert(_bufferedWriter is not null);

		var buffer = _bufferedWriter.Buffer.Span[..length];
		_memStream?.Write(buffer);
		_bufferedWriter.Produce(length);
		MD5.AppendData(buffer);
	}

	public void ResizeStream(int fileSize)
	{
		_fileStream?.SetLength(fileSize);
		// REMOVED: Memory stream should not be resized here.
		// The memory stream is managed separately and resizing it here was causing issues
		// with chunk transformations. Only the file stream needs to be resized at this point.
		// _memStream?.SetLength(fileSize);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_fileStream?.Dispose();
			DisposeMemStream();
			MD5.Dispose();
		}

		base.Dispose(disposing);
	}

	public void FlushToDisk()
	{
		_fileStream?.Flush();
		_memStream?.Flush();
	}

	public void DisposeMemStream()
	{
		_memStream?.Dispose();
		_memStream = null;
	}
}
