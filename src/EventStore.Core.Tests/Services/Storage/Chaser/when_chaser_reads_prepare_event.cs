using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Storage.Chaser;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class WhenChaserReadsPrepareEvent<TLogFormat, TStreamId> : with_storage_chaser_service<TLogFormat, TStreamId>
{
	private Guid _eventId;
	private Guid _transactionId;

	public override async ValueTask When(CancellationToken token)
	{
		_eventId = Guid.NewGuid();
		_transactionId = Guid.NewGuid();

		var recordFactory = LogFormatHelper<TLogFormat, TStreamId>.RecordFactory;
		var streamId = LogFormatHelper<TLogFormat, TStreamId>.StreamId;
		var eventTypeId = LogFormatHelper<TLogFormat, TStreamId>.EventTypeId;

		var record = LogRecord.Prepare(
			factory: recordFactory,
			logPosition: 0,
			eventId: _eventId,
			correlationId: _transactionId,
			transactionPos: 0xDEAD,
			transactionOffset: 0xBEEF,
			eventStreamId: streamId,
			expectedVersion: 1234,
			timeStamp: new DateTime(2012, 12, 21),
			flags: PrepareFlags.SingleWrite,
			eventType: eventTypeId,
			data: new byte[] { 1, 2, 3, 4, 5 },
			metadata: new byte[] { 7, 17 });

		Assert.True(await Writer.Write(record, token) is (true, _));
		Writer.Flush();
	}
	[Test]
	public void prepare_ack_should_be_published()
	{
		AssertEx.IsOrBecomesTrue(() => PrepareAcks.Count == 1, msg: "PrepareAck msg not received");
		Assert.True(PrepareAcks.TryDequeue(out var prepareAck));
		Assert.AreEqual(_transactionId, prepareAck.CorrelationId);

	}

}
