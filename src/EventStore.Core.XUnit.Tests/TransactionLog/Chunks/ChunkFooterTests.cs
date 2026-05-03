using System;
using EventStore.Common.Utils;
using EventStore.Core.TransactionLog.Chunks;
using Xunit;

namespace EventStore.Core.XUnit.Tests.TransactionLog.Chunks;

public class ChunkFooterTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void can_round_trip(bool isCompleted)
	{
		Span<byte> hash = stackalloc byte[ChunkFooter.ChecksumSize];
		Random.Shared.NextBytes(hash);

		var source = new ChunkFooter(
			isCompleted: isCompleted,
			physicalDataSize: Random.Shared.Next(500, 600),
			logicalDataSize: Random.Shared.Next(600, 700),
			mapSize: Random.Shared.Next(500, 600).RoundUpToMultipleOf(12)) { MD5Hash = hash };

		var destination = new ChunkFooter(source.AsByteArray());

		Assert.Equal(source.IsCompleted, destination.IsCompleted);
		Assert.Equal(source.PhysicalDataSize, destination.PhysicalDataSize);
		Assert.Equal(source.LogicalDataSize, destination.LogicalDataSize);
		Assert.Equal(source.MapSize, destination.MapSize);
		Assert.Equal(source.MD5Hash, destination.MD5Hash);
	}

	[Fact]
	public void rejects_deprecated_position_map_footer()
	{
		var bytes = new ChunkFooter(
			isCompleted: true,
			physicalDataSize: 500,
			logicalDataSize: 600,
			mapSize: 0).AsByteArray();
		bytes[0] &= unchecked((byte)~2);

		Assert.Throws<Exception>(() => new ChunkFooter(bytes));
	}

	[Fact]
	public void accepts_zeroed_incomplete_footer()
	{
		var footer = new ChunkFooter(new byte[ChunkFooter.Size]);

		Assert.False(footer.IsCompleted);
		Assert.Equal(0, footer.PhysicalDataSize);
		Assert.Equal(0, footer.LogicalDataSize);
		Assert.Equal(0, footer.MapSize);
	}
}
