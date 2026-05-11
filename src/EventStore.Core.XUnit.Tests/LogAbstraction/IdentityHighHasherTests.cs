using EventStore.Core.LogAbstraction;
using Xunit;

namespace EventStore.Core.XUnit.Tests.LogAbstraction;

public class IdentityHighHasherTests
{
	[Theory]
	[InlineData(0, 0)]
	[InlineData(5, 0)]
	[InlineData(0xAAAA_BBBB, 0)]
	[InlineData(0xFFFF_FFFF, 0)]
	public void hashes_correctly(uint x, uint expected)
	{
		var sut = new IdentityHighHasher();
		Assert.Equal(expected, sut.Hash(x));
	}
}
