using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Tests.TransactionLog.Scavenging.Helpers;
using NUnit.Framework;
using ReadStreamResult = EventStore.Core.Services.Storage.ReaderIndex.ReadStreamResult;

namespace EventStore.Core.Tests.Services.Storage.Metastreams;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	ReadIndexResultOriginalStreamExistsTests<TLogFormat, TStreamId>
	: SimpleDbTestScenario<TLogFormat, TStreamId>
{

	protected override ValueTask<DbResult> CreateDb(TFChunkDbCreationHelper<TLogFormat, TStreamId> dbCreator,
		CancellationToken token)
	{
		return dbCreator.Chunk(
			Rec.Prepare(0, "existing_stream"),
			Rec.Commit(0, "existing_stream"),
			Rec.Prepare(1, "$existing_stream"),
			Rec.Commit(1, "$existing_stream")
		).CreateDb(token: token);
	}

	[Test]
	public void original_stream_exists_is_true_when_reading_metastream_for_existing_stream()
	{
		var metaStreamName = SystemStreams.MetastreamOf("existing_stream");
		var metaStreamId = _logFormat.StreamIds.LookupValue(metaStreamName);
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, metaStreamId, 0);
		Assert.True(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_false_when_reading_metastream_for_non_existent_stream()
	{
		var metaStreamName = SystemStreams.MetastreamOf("non_existent_stream");
		var metaStreamId = _logFormat.StreamIds.LookupValue(metaStreamName);
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, metaStreamId, 0);
		Assert.False(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_null_when_reading_existing_stream()
	{
		var streamId = _logFormat.StreamIds.LookupValue("existing_stream");
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, streamId, 0);
		Assert.IsNull(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_null_when_reading_non_existent_stream()
	{
		var streamId = _logFormat.StreamIds.LookupValue("non_existent_stream");
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, streamId, 0);
		Assert.IsNull(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_true_when_reading_metastream_for_existing_system_stream()
	{
		var metaStreamName = SystemStreams.MetastreamOf("$existing_stream");
		var metaStreamId = _logFormat.StreamIds.LookupValue(metaStreamName);
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, metaStreamId, 0);
		Assert.True(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_false_when_reading_metastream_for_non_existent_system_stream()
	{
		var metaStreamName = SystemStreams.MetastreamOf("$non_existent_stream");
		var metaStreamId = _logFormat.StreamIds.LookupValue(metaStreamName);
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, metaStreamId, 0);
		Assert.False(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_null_when_reading_existing_system_stream()
	{
		var streamId = _logFormat.StreamIds.LookupValue("$existing_stream");
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, streamId, 0);
		Assert.IsNull(read.OriginalStreamExists);
	}

	[Test]
	public void original_stream_exists_is_null_when_reading_non_existent_system_stream()
	{
		var streamId = _logFormat.StreamIds.LookupValue("$non_existent_stream");
		var read = ReadIndex.ReadEvent(IndexReader.UnspecifiedStreamName, streamId, 0);
		Assert.IsNull(read.OriginalStreamExists);
	}
}
