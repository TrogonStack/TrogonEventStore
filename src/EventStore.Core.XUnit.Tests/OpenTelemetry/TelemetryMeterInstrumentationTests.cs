using System;
using System.Reflection;
using EventStore.Core.Diagnostics;
using FluentAssertions;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class TelemetryMeterInstrumentationTests
{
	[Fact]
	public void PreservesBuiltInMeterNames()
	{
		TelemetryMeterInstrumentation.CoreName.Should().Be("EventStore.Core");
		TelemetryMeterInstrumentation.DotNetRuntimeName.Should().Be("System.Runtime");
		TelemetryMeterInstrumentation.KestrelName.Should().Be("Microsoft.AspNetCore.Server.Kestrel");
		TelemetryMeterInstrumentation.ProjectionsName.Should().Be("EventStore.Projections.Core");
	}

	[Fact]
	public void UsesTheEmbeddedAssemblyVersionForTheScope()
	{
		var informationalVersion = typeof(TelemetryMeterInstrumentation).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
			.InformationalVersion;

		TelemetryMeterInstrumentation.ScopeVersion.Should().Be(informationalVersion.Split('+', 2)[0]);
	}

	[Fact]
	public void IncludesBuiltInMetersOnceBeforeAdditionalMeters()
	{
		var names = TelemetryMeterInstrumentation.GetNames([
			TelemetryMeterInstrumentation.CoreName,
			"Custom.Component",
		]);

		names.Should().Equal(
			TelemetryMeterInstrumentation.CoreName,
			TelemetryMeterInstrumentation.ProjectionsName,
			TelemetryMeterInstrumentation.DotNetRuntimeName,
			TelemetryMeterInstrumentation.KestrelName,
			"Custom.Component");
	}

	[Fact]
	public void RejectsMissingAdditionalMeterCollection()
	{
		var action = () => TelemetryMeterInstrumentation.GetNames(null!);

		action.Should().Throw<ArgumentNullException>();
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void RejectsInvalidAdditionalMeterNames(string meterName)
	{
		var action = () => TelemetryMeterInstrumentation.GetNames([meterName]);

		action.Should().Throw<ArgumentException>();
	}
}
