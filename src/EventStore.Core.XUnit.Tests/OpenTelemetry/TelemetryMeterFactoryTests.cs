using System;
using EventStore.Core.Diagnostics;
using FluentAssertions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class TelemetryMeterFactoryTests
{
	[Fact]
	public void UsesTheCurrentInstrumentationVersionForTheScope()
	{
		using var meter = TelemetryMeterFactory.Create("test-scope");

		meter.Name.Should().Be("test-scope");
		meter.Version.Should().Be(TelemetryMeterInstrumentation.ScopeVersion);
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
