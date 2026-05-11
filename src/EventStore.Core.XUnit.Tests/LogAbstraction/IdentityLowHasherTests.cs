using EventStore.Core.LogAbstraction;
using Xunit;

namespace EventStore.Core.XUnit.Tests.LogAbstraction;

public class IdentityLowHasherTests
{
	[Theory]
	[InlineData(0, 0)]
	[InlineData(5, 5)]
	[InlineData(0xAAAA_BBBB, 0xAAAA_BBBB)]
	[InlineData(0xFFFF_FFFF, 0xFFFF_FFFF)]
	public void hashes_correctly(uint x, uint expected)
	{
		var sut = new IdentityLowHasher();
		Assert.Equal(expected, sut.Hash(x));
	}
}
