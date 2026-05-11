using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.ReaderIndex;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.CheckCommit;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_checking_deleted_stream_with_expected_version_no_stream<TLogFormat, TStreamId> :
	ReadIndexTestScenario<TLogFormat, TStreamId> {
	private const string HardDeletedStream = "hard-deleted-stream";
	private const string SoftDeletedStream = "soft-deleted-stream";
	private TStreamId _hardDeletedStreamId;
	private TStreamId _softDeletedStreamId;

	protected override async ValueTask WriteTestScenario(CancellationToken token) {
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
	}

	[Test]
	public async Task hard_deleted_stream_is_valid_for_check_only_write() {
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_hardDeletedStreamId,
			ExpectedVersion.NoStream,
			Array.Empty<Guid>(),
			streamMightExist: true,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.Ok, result.Decision);
		Assert.AreEqual(EventNumber.DeletedStream, result.CurrentVersion);
	}

	[Test]
	public async Task soft_deleted_stream_is_valid_for_check_only_write() {
		var result = await ReadIndex.IndexWriter.CheckCommit(
			_softDeletedStreamId,
			ExpectedVersion.NoStream,
			Array.Empty<Guid>(),
			streamMightExist: true,
			CancellationToken.None);

		Assert.AreEqual(CommitDecision.Ok, result.Decision);
		Assert.IsTrue(result.IsSoftDeleted);
	}
}
