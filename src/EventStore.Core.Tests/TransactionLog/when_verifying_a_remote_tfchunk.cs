using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture]
public class when_verifying_a_remote_tfchunk : SpecificationWithFilePerTestFixture
{
	private TFChunk _chunk;
	private ThrowingRemoteChunkHandle _remoteHandle;
	private IChunkHandle _originalHandle;
	private FieldInfo _handleField;
	private string _originalFilename;
	private FieldInfo _filenameField;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		_chunk = await TFChunkHelper.CreateNewChunk(Filename);
		await _chunk.Complete(CancellationToken.None);

		_remoteHandle = new ThrowingRemoteChunkHandle {
			Length = _chunk.FileSize
		};

		_handleField = typeof(TFChunk)
			.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!;
		_originalHandle = (IChunkHandle)_handleField.GetValue(_chunk)!;
		_handleField.SetValue(_chunk, _remoteHandle);

		_filenameField = typeof(TFChunk)
			.GetField("_filename", BindingFlags.NonPublic | BindingFlags.Instance)!;
		_originalFilename = (string)_filenameField.GetValue(_chunk)!;
		_filenameField.SetValue(_chunk,
			Path.Combine(Path.GetDirectoryName(_originalFilename)!, $"{Guid.NewGuid()}.missing"));
	}

	[OneTimeTearDown]
	public override void TestFixtureTearDown()
	{
		_handleField?.SetValue(_chunk, _originalHandle);
		_filenameField?.SetValue(_chunk, _originalFilename);
		_chunk.Dispose();
		base.TestFixtureTearDown();
	}

	[Test]
	public async Task hash_verification_does_not_require_a_local_chunk_file()
	{
		Assert.That(_chunk.IsRemote, Is.True);

		await _chunk.VerifyFileHash(CancellationToken.None);
	}

	[Test]
	public void cancelled_hash_verification_is_observed_before_returning()
	{
		using var cancellationTokenSource = new CancellationTokenSource();
		cancellationTokenSource.Cancel();

		Assert.ThrowsAsync<OperationCanceledException>(async () =>
			await _chunk.VerifyFileHash(cancellationTokenSource.Token));
	}

	private sealed class ThrowingRemoteChunkHandle : IChunkHandle
	{
		public int StreamRequests { get; private set; }
		public int ReadRequests { get; private set; }

		public long Length { get; set; }

		public FileAccess Access => FileAccess.Read;

		public string Name => "throwing-remote-handle";

		public void Flush() {
		}

		public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken token) =>
			ValueTask.FromException(new AssertionException("Remote hash verification should not write."));

		public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken token) {
			ReadRequests++;
			return ValueTask.FromException<int>(new AssertionException("Remote hash verification should not read."));
		}

		public ValueTask SetReadOnlyAsync(bool value, CancellationToken token) => ValueTask.CompletedTask;

		public Stream CreateStream() {
			StreamRequests++;
			throw new AssertionException("Remote hash verification should not acquire a stream.");
		}

		public void Dispose() {
		}
	}
}
