using System;
using System.Collections.Generic;
using System.Reflection;
using EventStore.Common.Log;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using Serilog;
using Xunit;

namespace EventStore.Core.XUnit.Tests.OpenTelemetry;

public class OpenTelemetryLoggerTests
{
	[Fact]
	public void DoesNotConfigureExporterWhenLogsDisabled()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
			})
			.Build();
		OtlpExporterOptions captured = null;

		new LoggerConfiguration().AddOpenTelemetryLogger(configuration, "test-node", options => captured = options);

		captured.Should().BeNull();
	}

	[Fact]
	public void UsesSharedOtlpConfigWhenNoPerSignalSectionExists()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
				["EventStore:OpenTelemetry:Otlp:Headers"] = "key=shared",
				["EventStore:OpenTelemetry:Logs:Enabled"] = "true",
			})
			.Build();
		OtlpExporterOptions captured = null;

		new LoggerConfiguration().AddOpenTelemetryLogger(configuration, "test-node", options => captured = options);

		captured.Should().NotBeNull();
		captured.Endpoint.Should().Be(new Uri("http://shared:4317"));
		captured.Headers.Should().Be("key=shared");
		captured.Protocol.Should().Be(OtlpExportProtocol.Grpc);
	}

	[Fact]
	public void PerSignalLogsOtlpOverridesSharedSettings()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:OpenTelemetry:Otlp:Endpoint"] = "http://shared:4317",
				["EventStore:OpenTelemetry:Otlp:Headers"] = "key=shared",
				["EventStore:OpenTelemetry:Logs:Enabled"] = "true",
				["EventStore:OpenTelemetry:Logs:Otlp:Endpoint"] = "http://logs:4317",
				["EventStore:OpenTelemetry:Logs:Otlp:Headers"] = "key=logs",
				["EventStore:OpenTelemetry:Logs:Otlp:Protocol"] = "HttpProtobuf",
			})
			.Build();
		OtlpExporterOptions captured = null;

		new LoggerConfiguration().AddOpenTelemetryLogger(configuration, "test-node", options => captured = options);

		captured.Should().NotBeNull();
		captured.Endpoint.Should().Be(new Uri("http://logs:4317"));
		captured.Headers.Should().Be("key=logs");
		captured.Protocol.Should().Be(OtlpExportProtocol.HttpProtobuf);
	}

	[Theory]
	[InlineData("http://logs:4318", "http://logs:4318/v1/logs")]
	[InlineData("http://logs:4318/", "http://logs:4318/v1/logs")]
	[InlineData("http://logs:4318/custom/logs", "http://logs:4318/custom/logs")]
	public void HttpProtobufLogsEndpointUsesSignalPathForBaseEndpoints(string endpoint, string expected)
	{
		var method = typeof(OpenTelemetryLogger).GetMethod(
			"GetHttpProtobufLogsEndpoint",
			BindingFlags.NonPublic | BindingFlags.Static);

		method.Should().NotBeNull();
		method!.Invoke(null, [new Uri(endpoint)]).Should().Be(expected);
	}
}
