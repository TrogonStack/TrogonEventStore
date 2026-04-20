using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Transforms.Identity;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
[NonParallelizable]
public class when_accessing_tfchunk_stream_synchronously : SpecificationWithFile
{
	private ILogger _originalLogger;
	private TFChunk _chunk;
	private CollectingSink _sink;

	[SetUp]
	public override async Task SetUp()
	{
		await base.SetUp();

		_originalLogger = Log.Logger;
		_sink = new CollectingSink();
		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Debug()
			.WriteTo.Sink(_sink)
			.CreateLogger();
	}

	[TearDown]
	public override void TearDown()
	{
		Log.Logger = _originalLogger;
		_chunk?.Dispose();
		base.TearDown();
	}

	[Test]
	public async Task default_local_chunk_io_does_not_warn_on_synchronous_reads()
	{
		await CreateChunk(asyncIO: false);

		using var stream = GetHandle().CreateStream();
		Assert.That(stream.CanTimeout, Is.False);
		ReadSingleByte(stream);

		Assert.That(LoggedWarnings(), Is.Empty);
	}

	[Test]
	public async Task experimental_async_local_chunk_io_uses_timeout_capable_stream()
	{
		await CreateChunk(asyncIO: true);

		using var stream = GetHandle().CreateStream();

		Assert.That(stream.CanTimeout, Is.True);
	}

	[Test]
	public void generic_chunk_handles_warn_on_synchronous_reads()
	{
		var handle = new TestChunkHandle();

		using var stream = ((IChunkHandle)handle).CreateStream();
		ReadSingleByte(stream);

		Assert.That(LoggedWarnings(), Has.Some.Contains("Synchronous reads should be uncommon."));
	}

	private async Task CreateChunk(bool asyncIO)
	{
		_chunk = await TFChunk.CreateNew(
			filename: Filename,
			chunkDataSize: 4096,
			chunkStartNumber: 0,
			chunkEndNumber: 0,
			isScavenged: false,
			inMem: false,
			unbuffered: false,
			writethrough: false,
			reduceFileCachePressure: false,
			asyncIO: asyncIO,
			tracker: new EventStore.Core.TransactionLog.Chunks.TFChunkTracker.NoOp(),
			transformFactory: new IdentityChunkTransformFactory(),
			token: CancellationToken.None);
	}

	private IChunkHandle GetHandle()
	{
		var handleField = typeof(TFChunk).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
		return (IChunkHandle)handleField.GetValue(_chunk)!;
	}

	private static void ReadSingleByte(Stream stream)
	{
		var buffer = new byte[1];
		var bytesRead = stream.Read(buffer, 0, buffer.Length);

		Assert.That(bytesRead, Is.EqualTo(1));
	}

	private string[] LoggedWarnings()
	{
		return _sink.Events
			.Where(x => x.Level == LogEventLevel.Warning)
			.Select(x => x.RenderMessage())
			.ToArray();
	}

	private sealed class TestChunkHandle : IChunkHandle
	{
		public long Length { get; set; } = 1;
		public string Name => "test-handle";
		public FileAccess Access => FileAccess.Read;

		public void Flush()
		{
		}

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token) =>
			ValueTask.CompletedTask;

		public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token)
		{
			if (!buffer.IsEmpty)
				buffer.Span[0] = 0x42;

			return new(1);
		}

		public ValueTask SetReadOnlyAsync(bool value, CancellationToken token) => ValueTask.CompletedTask;

		public void Dispose()
		{
		}
	}

	private sealed class CollectingSink : ILogEventSink
	{
		private readonly object _lock = new();
		public LogEvent[] Events
		{
			get
			{
				lock (_lock)
				{
					return _events.ToArray();
				}
			}
		}

		private readonly System.Collections.Generic.List<LogEvent> _events = [];

		public void Emit(LogEvent logEvent)
		{
			lock (_lock)
			{
				_events.Add(logEvent);
			}
		}
	}
}
