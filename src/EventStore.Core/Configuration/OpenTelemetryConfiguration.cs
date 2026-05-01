using System;
using EventStore.Common.Configuration;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

namespace EventStore.Core.Configuration;

public static class OpenTelemetryConfiguration
{
	public const string RootPrefix = "EventStore";
	public const string OpenTelemetryPrefix = $"{RootPrefix}:OpenTelemetry";
	public const string OtlpConfigPrefix = $"{OpenTelemetryPrefix}:Otlp";
	public const string OtlpLogsPrefix = $"{OpenTelemetryPrefix}:Logs";
	public const string OtlpLogsOtlpPrefix = $"{OpenTelemetryPrefix}:Logs:Otlp";
	public const string OtlpMetricsPrefix = $"{OpenTelemetryPrefix}:Metrics";
	public const string OtlpMetricsOtlpPrefix = $"{OpenTelemetryPrefix}:Metrics:Otlp";

	public static bool OtlpLogsEnabled(this IConfiguration configuration) =>
		configuration.GetValue<bool>($"{OtlpLogsPrefix}:Enabled");

	public static bool OtlpMetricsEnabled(this IConfiguration configuration, MetricsConfiguration metricsConfiguration) =>
		metricsConfiguration.Otlp.Enabled ||
		configuration.GetSection(OtlpMetricsOtlpPrefix).Exists() ||
		configuration.GetValue<bool>($"{OtlpMetricsPrefix}:Enabled");

	public static OtlpExporterOptions GetOtlpExporterOptions(
		this IConfiguration configuration,
		string perSignalOtlpPrefix,
		Action<OtlpExporterOptions> configure = null)
	{
		var options = configuration.GetSection(OtlpConfigPrefix).Get<OtlpExporterOptions>() ?? new();
		configuration.GetSection(perSignalOtlpPrefix).Bind(options);
		configure?.Invoke(options);
		return options;
	}

	public static void BindOtlpExporterOptions(
		this IConfiguration configuration,
		string perSignalOtlpPrefix,
		OtlpExporterOptions options)
	{
		configuration.GetSection(OtlpConfigPrefix).Bind(options);
		configuration.GetSection(perSignalOtlpPrefix).Bind(options);
	}
}
