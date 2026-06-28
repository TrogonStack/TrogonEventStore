using System;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexingSubscriptionOptionsTests
{
	[Fact]
	public void constructor_accepts_valid_values()
	{
		var options = new IndexingSubscriptionOptions(100, TimeSpan.FromSeconds(5));

		Assert.Equal(100, options.CheckpointCommitBatchSize);
		Assert.Equal(TimeSpan.FromSeconds(5), options.CheckpointCommitDelay);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void constructor_rejects_non_positive_checkpoint_commit_batch_size(int batchSize)
	{
		var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
			new IndexingSubscriptionOptions(batchSize, TimeSpan.FromSeconds(5)));

		Assert.Equal("checkpointCommitBatchSize", exception.ParamName);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void constructor_rejects_non_positive_checkpoint_commit_delay(int milliseconds)
	{
		var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
			new IndexingSubscriptionOptions(100, TimeSpan.FromMilliseconds(milliseconds)));

		Assert.Equal("checkpointCommitDelay", exception.ParamName);
	}
}
