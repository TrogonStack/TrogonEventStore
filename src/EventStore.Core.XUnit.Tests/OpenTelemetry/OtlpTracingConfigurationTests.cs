using System;
using System.Collections.Generic;
using EventStore.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class OtlpTracingConfigurationTests
{
	[Fact]
	public void IsDisabledByDefault()
	{
		var configuration = new ConfigurationBuilder().Build();

		configuration.OtlpTracesEnabled().Should().BeFalse();
	}

	[Fact]
	public void DoesNotEnableTracesWhenOnlySharedOpenTelemetryOtlpSectionExists()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
			})
			.Build();

		configuration.OtlpTracesEnabled().Should().BeFalse();
	}

	[Fact]
	public void EnablesTracesWhenRuntimeSwitchUsesSharedOpenTelemetryOtlpSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Traces:Enabled"] = "true",
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
			})
			.Build();

		var options = configuration.GetOtlpExporterOptions(OpenTelemetryConfiguration.OtlpTracesOtlpPrefix);

		configuration.OtlpTracesEnabled().Should().BeTrue();
		options.Endpoint.Should().Be(new Uri("http://shared:4317"));
	}

	[Fact]
	public void EnablesTracesWhenPerSignalOpenTelemetryOtlpSectionExists()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Traces:Otlp:Endpoint"] = "http://traces:4317",
			})
			.Build();

		configuration.OtlpTracesEnabled().Should().BeTrue();
	}

	[Fact]
	public void PerSignalTracesOtlpOverridesSharedSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
				["EventStore:OpenTelemetry:Otlp:Headers"] = "key=shared",
				["EventStore:OpenTelemetry:Traces:Otlp:Endpoint"] = "http://traces:4317",
				["EventStore:OpenTelemetry:Traces:Otlp:Headers"] = "key=traces",
				["EventStore:OpenTelemetry:Traces:Otlp:Protocol"] = "HttpProtobuf",
			})
			.Build();

		var options = configuration.GetOtlpExporterOptions(OpenTelemetryConfiguration.OtlpTracesOtlpPrefix);

		options.Endpoint.Should().Be(new Uri("http://traces:4317"));
		options.Headers.Should().Be("key=traces");
		options.Protocol.Should().Be(OtlpExportProtocol.HttpProtobuf);
	}
}
