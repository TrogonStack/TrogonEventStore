using EventStore.Core.Data;
using Xunit;

namespace EventStore.Core.XUnit.Tests.CoreData;

public class EventSizeOnDiskTests
{
	[Fact]
	public void includes_data_metadata_and_event_type_bytes()
	{
		var size = Event.SizeOnDisk(
			eventType: "type",
			data: [1, 2, 3],
			metadata: [4, 5]);

		Assert.Equal(13, size);
	}

	[Theory]
	[InlineData(null, null, 8)]
	[InlineData(null, 2, 10)]
	[InlineData(3, null, 11)]
	public void treats_missing_payload_sections_as_zero(int? dataLength, int? metadataLength, int expectedSize)
	{
		var size = Event.SizeOnDisk(
			eventType: "type",
			data: dataLength is { } data ? new byte[data] : null,
			metadata: metadataLength is { } metadata ? new byte[metadata] : null);

		Assert.Equal(expectedSize, size);
	}
}
