using System.Runtime;

namespace EventStore.SystemRuntime.Tests;

public class RuntimeInformationTests
{
	[Fact]
	public void CanGetRuntimeVersion() => Assert.NotNull(RuntimeInformation.RuntimeVersion);

	[Fact]
	public void CanGetRuntimeMode() => Assert.True(RuntimeInformation.RuntimeMode > 0);
}
