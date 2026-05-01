using System;
using System.Collections.Generic;
using EventStore.Common.Configuration;
using EventStore.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Metrics;

public class OtlpMetricsConfigurationTests
{
	[Fact]
	public void IsDisabledByDefault()
	{
		var configuration = new ConfigurationBuilder().Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.Otlp.Enabled.Should().BeFalse();
	}

	[Fact]
	public void BindsOtlpSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:Metrics:Otlp:Enabled"] = "true",
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);

		metrics.Otlp.Enabled.Should().BeTrue();
		configuration.OtlpMetricsEnabled(metrics).Should().BeTrue();
	}

	[Fact]
	public void DoesNotEnableMetricsWhenOnlySharedOpenTelemetryOtlpSectionExists()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);

		configuration.OtlpMetricsEnabled(metrics).Should().BeFalse();
	}

	[Fact]
	public void EnablesMetricsWhenRuntimeSwitchUsesSharedOpenTelemetryOtlpSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Metrics:Enabled"] = "true",
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);
		var options = configuration.GetOtlpExporterOptions(OpenTelemetryConfiguration.OtlpMetricsOtlpPrefix);

		configuration.OtlpMetricsEnabled(metrics).Should().BeTrue();
		options.Endpoint.Should().Be(new Uri("http://shared:4317"));
	}

	[Fact]
	public void EnablesMetricsWhenPerSignalOpenTelemetryOtlpSectionExists()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Metrics:Otlp:Endpoint"] = "http://metrics:4317",
			})
			.Build();

		var metrics = MetricsConfiguration.Get(configuration);

		configuration.OtlpMetricsEnabled(metrics).Should().BeTrue();
	}

	[Fact]
	public void PerSignalMetricsOtlpOverridesSharedSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
				["EventStore:OpenTelemetry:Otlp:Headers"] = "key=shared",
				["EventStore:OpenTelemetry:Metrics:Otlp:Endpoint"] = "http://metrics:4317",
				["EventStore:OpenTelemetry:Metrics:Otlp:Headers"] = "key=metrics",
				["EventStore:OpenTelemetry:Metrics:Otlp:Protocol"] = "HttpProtobuf",
			})
			.Build();

		var options = configuration.GetOtlpExporterOptions(OpenTelemetryConfiguration.OtlpMetricsOtlpPrefix);

		options.Endpoint.Should().Be(new Uri("http://metrics:4317"));
		options.Headers.Should().Be("key=metrics");
		options.Protocol.Should().Be(OtlpExportProtocol.HttpProtobuf);
	}
}
