using System;
using EventStore.Core.Services.Storage.Indexing;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class AllStreamsIndexingEventSourceFactoryTests
{
	[Fact]
	public void constructor_rejects_missing_publisher()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new AllStreamsIndexingEventSourceFactory(null!));

		Assert.Equal("publisher", exception.ParamName);
	}
}
