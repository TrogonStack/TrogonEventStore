using System;
using EventStore.Core.Services.Storage.Indexing;
using EventStore.Core.Services.Transport.Common;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexCheckpointTests
{
	[Theory]
	[InlineData(0, 0)]
	[InlineData(10, 5)]
	public void constructor_accepts_valid_positions(long commitPosition, long preparePosition)
	{
		var checkpoint = new IndexCheckpoint(commitPosition, preparePosition);

		Assert.Equal(commitPosition, checkpoint.CommitPosition);
		Assert.Equal(preparePosition, checkpoint.PreparePosition);
	}

	[Fact]
	public void constructor_rejects_negative_commit_position()
	{
		var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new IndexCheckpoint(-1, 0));

		Assert.Equal("commitPosition", exception.ParamName);
	}

	[Fact]
	public void constructor_rejects_negative_prepare_position()
	{
		var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new IndexCheckpoint(0, -1));

		Assert.Equal("preparePosition", exception.ParamName);
	}

	[Fact]
	public void constructor_rejects_commit_position_before_prepare_position()
	{
		var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new IndexCheckpoint(5, 10));

		Assert.Equal("CommitPosition", exception.ParamName);
	}

	[Fact]
	public void converts_to_all_stream_position()
	{
		var checkpoint = new IndexCheckpoint(10, 5);

		var position = checkpoint.ToPosition();

		Assert.Equal(Position.FromInt64(10, 5), position);
	}
}
