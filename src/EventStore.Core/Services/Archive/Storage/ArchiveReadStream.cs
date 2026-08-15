using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EventStore.Core.Services.Archive.Storage;

internal sealed class ArchiveReadStream(
	Stream inner,
	IArchiveMetrics metrics,
	ArchiveOperation operation,
	long started) : Stream
{
	private int _recorded;

	public override bool CanRead => inner.CanRead;
	public override bool CanSeek => inner.CanSeek;
	public override bool CanWrite => inner.CanWrite;
	public override long Length => inner.Length;
	public override long Position
	{
		get => inner.Position;
		set => inner.Position = value;
	}

	public override void Flush() => inner.Flush();

	public override int Read(byte[] buffer, int offset, int count)
	{
		try
		{
			var read = inner.Read(buffer, offset, count);
			RecordEndOfStream(read, count);
			return read;
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
	}

	public override int Read(Span<byte> buffer)
	{
		try
		{
			var read = inner.Read(buffer);
			RecordEndOfStream(read, buffer.Length);
			return read;
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
	}

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		try
		{
			var read = await inner.ReadAsync(buffer, cancellationToken);
			RecordEndOfStream(read, buffer.Length);
			return read;
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
	}

	public override async Task<int> ReadAsync(
		byte[] buffer,
		int offset,
		int count,
		CancellationToken cancellationToken)
	{
		try
		{
			var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
			RecordEndOfStream(read, count);
			return read;
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
	}

	public override int ReadByte()
	{
		try
		{
			var value = inner.ReadByte();
			if (value < 0)
			{
				RecordSuccess();
			}

			return value;
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
	}

	public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
	public override void SetLength(long value) => inner.SetLength(value);
	public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

	protected override void Dispose(bool disposing)
	{
		if (!disposing)
		{
			base.Dispose(disposing);
			return;
		}

		try
		{
			inner.Dispose();
			RecordSuccess();
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
		finally
		{
			base.Dispose(disposing);
		}
	}

	public override async ValueTask DisposeAsync()
	{
		try
		{
			await inner.DisposeAsync();
			RecordSuccess();
		}
		catch (OperationCanceledException)
		{
			Ignore();
			throw;
		}
		catch
		{
			RecordFailure();
			throw;
		}
		finally
		{
			GC.SuppressFinalize(this);
		}
	}

	private void RecordEndOfStream(int bytesRead, int requestedBytes)
	{
		if (bytesRead == 0 && requestedBytes > 0)
		{
			RecordSuccess();
		}
	}

	private void RecordSuccess() => Record(succeeded: true);

	private void RecordFailure()
		=> Record(succeeded: false);

	private void Ignore() => Interlocked.Exchange(ref _recorded, 1);

	private void Record(bool succeeded)
	{
		if (Interlocked.Exchange(ref _recorded, 1) != 0)
		{
			return;
		}

		if (!succeeded)
		{
			metrics.RecordFailure(operation);
		}

		metrics.RecordRead(operation, Stopwatch.GetElapsedTime(started), succeeded);
	}
}
