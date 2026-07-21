using System;
using System.Linq;
using EventStore.Common.Utils;
using EventStore.Core.Diagnostics;
using FluentAssertions;
using TrogonEventStore.SemanticConventions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class TelemetryServiceIdentityTests
{
	[Fact]
	public void UsesTheSameIdentityForAttributeDictionariesAndResources()
	{
		var identity = TelemetryServiceIdentity.ForComponent("test-node");
		var dictionary = identity.CreateAttributeDictionary();
		var resource = identity.CreateResourceBuilder().Build().Attributes
			.ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

		dictionary.Should().Contain(AttributeNames.ServiceName, "eventstore");
		dictionary.Should().Contain(AttributeNames.ServiceInstanceId, "test-node");
		dictionary.Should().Contain(AttributeNames.ServiceVersion, VersionInfo.Version);
		dictionary.Should().ContainKey(AttributeNames.ProcessCreationTime).WhoseValue.Should().BeOfType<string>();
		dictionary.Should().Contain(AttributeNames.ProcessPid, Environment.ProcessId);
		dictionary.Should().ContainKey(AttributeNames.ProcessExecutableName).WhoseValue.Should().BeOfType<string>();
		dictionary.Should().Contain(AttributeNames.ProcessRuntimeName, ".NET");
		dictionary.Should().Contain(AttributeNames.ProcessRuntimeVersion, Environment.Version.ToString());
		dictionary.Should().Contain(AttributeNames.HostName, Environment.MachineName);
		dictionary.Should().ContainKey(AttributeNames.HostArch).WhoseValue.Should().BeOfType<string>();
		resource.Should().Contain(dictionary);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void RejectsMissingComponentNames(string componentName)
	{
		var action = () => TelemetryServiceIdentity.ForComponent(componentName);

		action.Should().Throw<ArgumentException>();
	}
}
