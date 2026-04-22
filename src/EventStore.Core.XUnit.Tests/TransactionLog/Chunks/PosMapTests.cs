using System;
using System.Buffers.Binary;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using Xunit;

namespace EventStore.Core.XUnit.Tests.TransactionLog.Chunks;

public class PosMapTests
{
	[Fact]
	public void can_parse_old_format_from_span()
	{
		Span<byte> source = stackalloc byte[PosMap.DeprecatedSize];
		var actualPos = 1234;
		var logPos = 5678;
		BinaryPrimitives.WriteUInt64LittleEndian(source, ((ulong)(uint)logPos << 32) | (uint)actualPos);

		var destination = PosMap.FromOldFormat(source);

		Assert.Equal(logPos, destination.LogPos);
		Assert.Equal(actualPos, destination.ActualPos);
	}
}
