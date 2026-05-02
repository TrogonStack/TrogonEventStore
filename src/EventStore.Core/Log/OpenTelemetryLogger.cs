using System;
using System.Collections.Generic;
using EventStore.Common.Utils;
using EventStore.Core.Configuration;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using Serilog;
using Serilog.Filters;
using Serilog.Sinks.OpenTelemetry;

namespace EventStore.Common.Log;

public static class OpenTelemetryLogger
{
	public static LoggerConfiguration AddOpenTelemetryLogger(
		this LoggerConfiguration loggerConfiguration,
		IConfiguration configuration,
		string componentName,
		Action<OtlpExporterOptions> configureOtlp = null)
	{
		if (configuration is null || !configuration.OtlpLogsEnabled())
			return loggerConfiguration;

		var logExporterConfig = configuration
			.GetSection(OpenTelemetryConfiguration.OtlpLogsPrefix)
			.Get<LogRecordExportProcessorOptions>() ?? new();
		var otlpExporterConfig = configuration.GetOtlpExporterOptions(
			OpenTelemetryConfiguration.OtlpLogsOtlpPrefix,
			configureOtlp);

		return loggerConfiguration.WriteTo.Logger(sinkConfiguration => sinkConfiguration
			.Filter.ByExcluding(Matching.FromSource("REGULAR-STATS-LOGGER"))
			.WriteTo.OpenTelemetry(options =>
			{
				options.ResourceAttributes = new Dictionary<string, object>
				{
					["service.name"] = "eventstore",
					["service.instance.id"] = componentName,
					["service.version"] = VersionInfo.Version
				};
				options.Protocol = otlpExporterConfig.Protocol switch
				{
					OtlpExportProtocol.Grpc => OtlpProtocol.Grpc,
					OtlpExportProtocol.HttpProtobuf => OtlpProtocol.HttpProtobuf,
					_ => throw new ArgumentOutOfRangeException(
						nameof(otlpExporterConfig.Protocol),
						$">{otlpExporterConfig.Protocol}<",
						"Invalid protocol for OTLP exporter.")
				};
				if (otlpExporterConfig.Protocol == OtlpExportProtocol.HttpProtobuf)
				{
					options.Endpoint = null;
					options.LogsEndpoint = GetHttpProtobufLogsEndpoint(otlpExporterConfig.Endpoint);
				}
				else
				{
					options.Endpoint = otlpExporterConfig.Endpoint.AbsoluteUri;
				}
				options.BatchingOptions.BatchSizeLimit =
					logExporterConfig.BatchExportProcessorOptions.MaxExportBatchSize;
				options.BatchingOptions.BufferingTimeLimit = TimeSpan.FromMilliseconds(
					logExporterConfig.BatchExportProcessorOptions.ScheduledDelayMilliseconds);
				options.BatchingOptions.QueueLimit =
					logExporterConfig.BatchExportProcessorOptions.MaxQueueSize;
			}, getConfigurationVariable: name => name switch
			{
				"OTEL_EXPORTER_OTLP_HEADERS" => otlpExporterConfig.Headers ?? Environment.GetEnvironmentVariable(name),
				_ => Environment.GetEnvironmentVariable(name),
			}));
	}

	private static string GetHttpProtobufLogsEndpoint(Uri endpoint)
	{
		var builder = new UriBuilder(endpoint);
		var path = builder.Path.Trim('/');

		if (string.IsNullOrWhiteSpace(path))
			builder.Path = "v1/logs";

		return builder.Uri.AbsoluteUri;
	}
}
