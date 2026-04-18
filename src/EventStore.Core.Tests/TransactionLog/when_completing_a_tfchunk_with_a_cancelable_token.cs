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

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		_chunk = await TFChunkHelper.CreateNewChunk(Filename);
		var record = LogRecord.Commit(0, Guid.NewGuid(), 0, 0);
		var writeResult = await _chunk.TryAppend(record, CancellationToken.None);
		Assert.That(writeResult.Success, Is.True);

		var handleField = typeof(TFChunk)
			.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
		var originalHandle = (IChunkHandle)handleField.GetValue(_chunk)!;
		_observingHandle = new ObservingChunkHandle(originalHandle);
		handleField.SetValue(_chunk, _observingHandle);
	}

	[OneTimeTearDown]
	public override void TestFixtureTearDown()
	{
		_chunk?.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public async Task completes_the_read_only_transition_without_forwarding_the_cancelable_token()
	{
		using var cancellationTokenSource = new CancellationTokenSource();

		await _chunk.Complete(cancellationTokenSource.Token);

		Assert.That(_chunk.IsReadOnly, Is.True);
		Assert.That(_observingHandle.SetReadOnlyCalls, Is.EqualTo(1));
		Assert.That(_observingHandle.SawCancelableToken, Is.False);
	}

	private sealed class ObservingChunkHandle(IChunkHandle inner) : IChunkHandle
	{
		public int SetReadOnlyCalls { get; private set; }
		public bool SawCancelableToken { get; private set; }

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
			SawCancelableToken |= token.CanBeCanceled;
			return inner.SetReadOnlyAsync(value, token);
		}

		public Stream CreateStream() => inner.CreateStream();

		public void Dispose() => inner.Dispose();
	}
}
