using System;
using EventStore.Common.Utils;
using EventStore.Core.Diagnostics;
using FluentAssertions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class TelemetryMeterFactoryTests
{
	[Fact]
	public void UsesTheCurrentServerVersionForTheInstrumentationScope()
	{
		using var meter = TelemetryMeterFactory.Create("test-scope");

		meter.Name.Should().Be("test-scope");
		meter.Version.Should().Be(VersionInfo.Version);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void RejectsMissingInstrumentationScopeNames(string instrumentationScopeName)
	{
		var action = () => TelemetryMeterFactory.Create(instrumentationScopeName);

		action.Should().Throw<ArgumentException>();
	}
}
