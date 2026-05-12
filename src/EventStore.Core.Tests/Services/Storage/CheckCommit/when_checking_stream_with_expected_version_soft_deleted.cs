using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.CheckCommit;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_checking_stream_with_expected_version_soft_deleted<TLogFormat, TStreamId> :
	ReadIndexTestScenario<TLogFormat, TStreamId>
{
	private const string HardDeletedStream = "hard-deleted-stream";
	private const string SoftDeletedStream = "soft-deleted-stream";
	private const string ExistingStream = "existing-stream";
	private const string NewStream = "new-stream";
	private TStreamId _hardDeletedStreamId;
	private TStreamId _softDeletedStreamId;
	private TStreamId _existingStreamId;
	private TStreamId _newStreamId;

	protected override async ValueTask WriteTestScenario(CancellationToken token)
	{
		(_hardDeletedStreamId, _) = await GetOrReserve(HardDeletedStream, token);
		await WriteSingleEvent(HardDeletedStream, 0, "event", token: token);
		await WriteDelete(HardDeletedStream, token);

		(_softDeletedStreamId, _) = await GetOrReserve(SoftDeletedStream, token);
		await WriteSingleEvent(SoftDeletedStream, 0, "event", token: token);
		await WriteStreamMetadata(
			SoftDeletedStream,
			0,
			new StreamMetadata(truncateBefore: EventNumber.DeletedStream).ToJsonString(),
			token: token);

		(_existingStreamId, _) = await GetOrReserve(ExistingStream, token);
		await WriteSingleEvent(ExistingStream, 0, "event", token: token);

		(_newStreamId, _) = await GetOrReserve(NewStream, token);
	}

	[Test]
	public async Task soft_deleted_stream_accepts_new_events()
	{
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_softDeletedStreamId,
			ExpectedVersion.SoftDeleted,
			[Guid.NewGuid()],
			streamMightExist: true,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.Ok, result.Decision);
		Assert.IsTrue(result.IsSoftDeleted);
	}

	[Test]
	public async Task soft_deleted_stream_accepts_check_only_write()
	{
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_softDeletedStreamId,
			ExpectedVersion.SoftDeleted,
			Array.Empty<Guid>(),
			streamMightExist: true,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.Ok, result.Decision);
		Assert.IsTrue(result.IsSoftDeleted);
	}

	[Test]
	public async Task hard_deleted_stream_is_rejected()
	{
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_hardDeletedStreamId,
			ExpectedVersion.SoftDeleted,
			[Guid.NewGuid()],
			streamMightExist: true,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.Deleted, result.Decision);
	}

	[Test]
	public async Task existing_stream_is_rejected()
	{
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_existingStreamId,
			ExpectedVersion.SoftDeleted,
			[Guid.NewGuid()],
			streamMightExist: true,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.WrongExpectedVersion, result.Decision);
	}

	[Test]
	public async Task new_stream_fast_path_is_rejected()
	{
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_newStreamId,
			ExpectedVersion.SoftDeleted,
			[Guid.NewGuid()],
			streamMightExist: false,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.WrongExpectedVersion, result.Decision);
	}
}
