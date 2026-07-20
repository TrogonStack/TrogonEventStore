using System;
using System.Reflection;
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
		var informationalVersion = typeof(TelemetryMeterFactory).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;

		meter.Name.Should().Be("test-scope");
		meter.Version.Should().Be(informationalVersion.Split('+', 2)[0]);
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
