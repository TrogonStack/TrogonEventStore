using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DotNext;
using DotNext.IO;
using Microsoft.Win32.SafeHandles;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk;

/// <summary>
/// Wraps local chunk file access and makes the sync-vs-async file I/O choice explicit.
/// When opened without <see cref="FileOptions.Asynchronous"/>, cancellation is only observed
/// before the synchronous filesystem call starts; callers that need cooperative mid-operation
/// cancellation should prefer asynchronous handles.
/// </summary>
internal sealed class ChunkFileHandle : Disposable, IChunkHandle
{
	private readonly SafeFileHandle _handle;
	private readonly string _path;
	private readonly bool _asynchronous;

	public ChunkFileHandle(string path, FileStreamOptions options)
	{
		Debug.Assert(options is not null);
		Debug.Assert(path is { Length: > 0 });

		_handle = File.OpenHandle(path, options.Mode, options.Access, options.Share, options.Options,
			options.PreallocationSize);
		_path = path;
		_asynchronous = options.Options.HasFlag(FileOptions.Asynchronous);
		Access = options.Access;

		SetReadOnly(_handle, options.Access.HasFlag(FileAccess.Write) is false);
	}

	public void Flush() => RandomAccess.FlushToDisk(_handle);

	/// <summary>
	/// Writes to the chunk handle at the requested offset.
	/// For synchronous handles, <paramref name="token"/> is checked before the write begins but
	/// cannot interrupt a blocking local filesystem write once it has started.
	/// </summary>
	public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token)
	{
		if (_asynchronous)
			return RandomAccess.WriteAsync(_handle, data, offset, token);

		if (token.IsCancellationRequested)
			return ValueTask.FromCanceled(token);

		try
		{
			Write(data.Span, offset);
			return ValueTask.CompletedTask;
		}
		catch (Exception ex)
		{
			return ValueTask.FromException(ex);
		}
	}

	/// <summary>
	/// Reads from the chunk handle at the requested offset.
	/// For synchronous handles, <paramref name="token"/> is checked before the read begins but
	/// cannot interrupt a blocking local filesystem read once it has started.
	/// </summary>
	public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token)
	{
		if (_asynchronous)
			return RandomAccess.ReadAsync(_handle, buffer, offset, token);

		if (token.IsCancellationRequested)
			return ValueTask.FromCanceled<int>(token);

		try
		{
			return new(Read(buffer.Span, offset));
		}
		catch (Exception ex)
		{
			return ValueTask.FromException<int>(ex);
		}
	}

	public long Length
	{
		get => RandomAccess.GetLength(_handle);
		set => RandomAccess.SetLength(_handle, value);
	}

	public string Name => _path;

	public FileAccess Access { get; }

	internal bool Asynchronous => _asynchronous;

	public ValueTask SetReadOnlyAsync(bool value, CancellationToken token)
	{
		ValueTask task;
		if (token.IsCancellationRequested)
		{
			task = ValueTask.FromCanceled(token);
		}
		else
		{
			task = ValueTask.CompletedTask;
			try
			{
				SetReadOnly(_handle, value);
			}
			catch (Exception e)
			{
				task = ValueTask.FromException(e);
			}
		}

		return task;
	}

	internal void Write(ReadOnlySpan<byte> data, long offset) => RandomAccess.Write(_handle, data, offset);

	internal int Read(Span<byte> buffer, long offset) => RandomAccess.Read(_handle, buffer, offset);

	internal Stream CreateSynchronousStream(bool leaveOpen) => new SynchronousStream(this, leaveOpen);

	private static void SetReadOnly(SafeFileHandle handle, bool value)
	{
		var flags = value
			? FileAttributes.ReadOnly | FileAttributes.NotContentIndexed
			: FileAttributes.NotContentIndexed;

		if (OperatingSystem.IsWindows())
		{
			try
			{
				File.SetAttributes(handle, flags);
			}
			catch (UnauthorizedAccessException)
			{
				// suppress exception
			}
		}
		else
		{
			File.SetAttributes(handle, flags);
		}
	}

	private sealed class SynchronousStream(ChunkFileHandle handle, bool leaveOpen) : RandomAccessStream
	{
		public override void Flush() => handle.Flush();

		public override void SetLength(long value) => handle.Length = value;

		public override bool CanRead => handle.Access.HasFlag(FileAccess.Read);

		public override bool CanSeek => true;

		public override bool CanWrite => handle.Access.HasFlag(FileAccess.Write);

		public override bool CanTimeout => false;

		public override long Length => handle.Length;

		protected override void Write(ReadOnlySpan<byte> buffer, long offset)
		{
			if (buffer.IsEmpty)
				return;

			handle.Write(buffer, offset);
		}

		protected override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken token) =>
			handle.WriteAsync(buffer, offset, token);

		protected override int Read(Span<byte> buffer, long offset)
			=> buffer.IsEmpty ? 0 : handle.Read(buffer, offset);

		protected override ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token) =>
			handle.ReadAsync(buffer, offset, token);

		protected override void Dispose(bool disposing)
		{
			if (disposing && !leaveOpen)
				handle.Dispose();

			base.Dispose(disposing);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_handle.Dispose();
		}

		base.Dispose(disposing);
	}
}
