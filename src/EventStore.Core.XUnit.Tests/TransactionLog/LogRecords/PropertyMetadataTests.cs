using System;
using EventStore.Core.Data;
using EventStore.Core.LogV2;
using EventStore.Core.TransactionLog.LogRecords;
using Xunit;

namespace EventStore.Core.XUnit.Tests.TransactionLog.LogRecords;

public class PropertyMetadataTests
{
	[Fact]
	public void events_default_to_custom_metadata()
	{
		var evnt = new Event(
			Guid.NewGuid(),
			"test-event",
			isJson: true,
			data: [1, 2, 3],
			metadata: [4, 5, 6]);

		Assert.False(evnt.IsPropertyMetadata);
		Assert.Equal([4, 5, 6], evnt.Metadata);
	}

	[Fact]
	public void events_can_mark_the_metadata_slot_as_properties()
	{
		var evnt = new Event(
			Guid.NewGuid(),
			"test-event",
			isJson: false,
			data: [1, 2, 3],
			isPropertyMetadata: true,
			metadata: [4, 5, 6]);

		Assert.True(evnt.IsPropertyMetadata);
		Assert.Equal([4, 5, 6], evnt.Metadata);
	}

	[Fact]
	public void transaction_write_carries_the_property_metadata_flag()
	{
		var propertyMetadata = new byte[] { 4, 5, 6 };
		var prepare = LogRecord.TransactionWrite(
			new LogV2RecordFactory(),
			logPosition: 0,
			correlationId: Guid.NewGuid(),
			eventId: Guid.NewGuid(),
			transactionPos: 0,
			transactionOffset: 0,
			eventStreamId: "test-stream",
			eventType: "test-event",
			data: [1, 2, 3],
			metadata: propertyMetadata,
			isJson: false,
			isPropertyMetadata: true);

		Assert.True(prepare.Flags.HasAllOf(PrepareFlags.IsPropertyMetadata));

		var record = new EventRecord(0, prepare, "test-stream", "test-event");

		Assert.Equal(propertyMetadata, record.Metadata.ToArray());
		Assert.Equal(propertyMetadata, record.Properties.ToArray());
	}

	[Fact]
	public void custom_metadata_is_not_exposed_as_properties()
	{
		var customMetadata = new byte[] { 4, 5, 6 };
		var prepare = LogRecord.TransactionWrite(
			new LogV2RecordFactory(),
			logPosition: 0,
			correlationId: Guid.NewGuid(),
			eventId: Guid.NewGuid(),
			transactionPos: 0,
			transactionOffset: 0,
			eventStreamId: "test-stream",
			eventType: "test-event",
			data: [1, 2, 3],
			metadata: customMetadata,
			isJson: true);

		Assert.False(prepare.Flags.HasAllOf(PrepareFlags.IsPropertyMetadata));

		var record = new EventRecord(0, prepare, "test-stream", "test-event");

		Assert.Equal(customMetadata, record.Metadata.ToArray());
		Assert.Empty(record.Properties.ToArray());
	}
}
