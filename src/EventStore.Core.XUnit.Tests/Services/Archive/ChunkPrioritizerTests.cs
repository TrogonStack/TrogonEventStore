using System;
using EventStore.Core.Services.Archive;
using EventStore.Core.Services.Archive.Archiver;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archiver;

public class ChunkPrioritizerTests
{
	[Theory]
	// start takes priority
	[InlineData(1, 0, 2, 0, -1)]
	[InlineData(1, 2, 2, 1, -1)]
	[InlineData(2, 0, 1, 0, 1)]
	[InlineData(2, 1, 1, 2, 1)]
	// end is used if start is equal
	[InlineData(0, 1, 0, 2, -1)]
	[InlineData(0, 2, 0, 1, 1)]
	[InlineData(0, 0, 0, 0, 0)]
	public void compares_correctly(int xStart, int xEnd, int yStart, int yEnd, int expected)
	{
		var x = new Commands.ArchiveChunk { ChunkPath = "x path", ChunkStartNumber = xStart, ChunkEndNumber = xEnd, };

		var y = new Commands.ArchiveChunk { ChunkPath = "y path", ChunkStartNumber = yStart, ChunkEndNumber = yEnd, };

		var sut = new ChunkPrioritizer();
		Assert.Equal(expected, sut.Compare(x, y));
	}
}
