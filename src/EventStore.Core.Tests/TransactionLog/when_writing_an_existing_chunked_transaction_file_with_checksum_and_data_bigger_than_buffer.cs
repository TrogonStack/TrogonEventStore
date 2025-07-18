using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Tests.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using EventStore.Core.TransactionLog.FileNamingStrategy;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Plugins.Transforms;
using NUnit.Framework;

namespace EventStore.Core.Tests.TransactionLog;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class
	when_writing_an_existing_chunked_transaction_file_with_checksum_and_data_bigger_than_buffer<TLogFormat, TStreamId> :
		SpecificationWithDirectory
{
	private readonly Guid _correlationId = Guid.NewGuid();
	private readonly Guid _eventId = Guid.NewGuid();
	private InMemoryCheckpoint _checkpoint;

	[Test]
	public async Task a_record_can_be_written()
	{
		var filename = GetFilePathFor("chunk-000000.000000");
		var chunkHeader = new ChunkHeader(TFChunk.CurrentChunkVersion, TFChunk.CurrentChunkVersion, 10000, 0, 0, false, Guid.NewGuid(), TransformType.Identity);
		var chunkBytes = chunkHeader.AsByteArray();
		var buf = new byte[ChunkHeader.Size + ChunkFooter.Size + chunkHeader.ChunkSize];
		Buffer.BlockCopy(chunkBytes, 0, buf, 0, chunkBytes.Length);
		File.WriteAllBytes(filename, buf);

		_checkpoint = new InMemoryCheckpoint(137);
		var db = new TFChunkDb(TFChunkHelper.CreateDbConfig(PathName, _checkpoint, new InMemoryCheckpoint(),
			chunkSize: chunkHeader.ChunkSize));
		await db.Open();

		var
			bytes = new byte[3994]; // this gives exactly 4097 size of record, with 3993 (rec size 4096) everything works fine!
		new Random().NextBytes(bytes);
		var writer = new TFChunkWriter(db);
		writer.Open();

		var recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

		var record = LogRecord.Prepare(
			factory: recordFactory,
			logPosition: 137,
			correlationId: _correlationId,
			eventId: _eventId,
			transactionPos: 789,
			transactionOffset: 543,
			eventStreamId: streamId,
			expectedVersion: 1234,
			timeStamp: new DateTime(2012, 12, 21),
			flags: PrepareFlags.SingleWrite,
			eventType: eventTypeId,
			data: bytes,
			metadata: new byte[] { 0x07, 0x17 });

		Assert.IsTrue(await writer.Write(record, CancellationToken.None) is (true, _));
		writer.Close();
		await db.DisposeAsync();

		Assert.AreEqual(record.GetSizeWithLengthPrefixAndSuffix() + 137, _checkpoint.Read());
		using (var filestream = File.Open(filename, FileMode.Open, FileAccess.Read))
		{
			filestream.Seek(ChunkHeader.Size + 137 + sizeof(int), SeekOrigin.Begin);
			var reader = new BinaryReader(filestream);
			var read = LogRecord.ReadFrom(reader, (int)reader.BaseStream.Length);
			Assert.AreEqual(record, read);
		}
	}
}
