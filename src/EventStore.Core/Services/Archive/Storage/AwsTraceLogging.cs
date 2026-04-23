using System;
using System.Diagnostics;
using System.Threading;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Serilog.Events;

namespace EventStore.Core.Services.Archive.Storage;

public sealed class AwsTraceSerilogListener : TraceListener
{
	private readonly Serilog.ILogger _logger;

	public AwsTraceSerilogListener() : this(
		Serilog.Log.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "Amazon"))
	{
	}

	public AwsTraceSerilogListener(Serilog.ILogger logger)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id,
		object data) =>
		Log(eventType, data);

	public override void TraceData(TraceEventCache eventCache, string source, TraceEventType eventType, int id,
		params object[] data) =>
		Log(eventType, data);

	public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id)
	{
	}

	public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id,
		string format, params object[] args)
	{
		if (format is null)
			return;

		WritePlainMessage(GetLogLevel(eventType), string.Format(format, args ?? Array.Empty<object>()));
	}

	public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType eventType, int id,
		string message)
	{
		if (message is null)
			return;

		WritePlainMessage(GetLogLevel(eventType), message);
	}

	public override void TraceTransfer(TraceEventCache eventCache, string source, int id, string message,
		Guid relatedActivityId)
	{
		if (message is null)
			return;

		WritePlainMessage(LogEventLevel.Verbose, $"{message} ({relatedActivityId})");
	}

	public override void Write(string message)
	{
		if (message is null)
			return;

		WritePlainMessage(LogEventLevel.Information, message);
	}

	public override void WriteLine(string message)
	{
		if (message is null)
			return;

		WritePlainMessage(LogEventLevel.Information, message);
	}

	private void Log(TraceEventType eventType, params object[] data)
	{
		if (data is null || data.Length is 0)
			return;

		var exception = data.Length > 1 ? data[1] as Exception : null;
		var logLevel = AdjustDefaultLevel(GetLogLevel(eventType), exception);

		switch (data[0])
		{
			case ILogMessage logMessage:
				_logger.Write(logLevel, exception, logMessage.Format, logMessage.Args);
				break;
			case string message:
				_logger.Write(logLevel, exception, "{AwsTraceMessage:l}", message);
				break;
			default:
			{
				var rendered = data[0]?.ToString();
				if (!string.IsNullOrEmpty(rendered))
					_logger.Write(logLevel, exception, "{AwsTraceMessage:l}", rendered);
				break;
			}
		}
	}

	private void WritePlainMessage(LogEventLevel logLevel, string message) =>
		_logger.Write(logLevel, "{AwsTraceMessage:l}", message);

	internal static LogEventLevel AdjustDefaultLevel(LogEventLevel logLevel, Exception exception) =>
		exception is AmazonS3Exception { ErrorCode: "NoSuchKey" or "InvalidRange" }
			or HttpErrorResponseException
			? LogEventLevel.Verbose
			: logLevel == LogEventLevel.Error
				? LogEventLevel.Warning
				: logLevel;

	private static LogEventLevel GetLogLevel(TraceEventType eventType) =>
		eventType switch
		{
			TraceEventType.Verbose => LogEventLevel.Verbose,
			TraceEventType.Information => LogEventLevel.Information,
			TraceEventType.Warning => LogEventLevel.Warning,
			TraceEventType.Error => LogEventLevel.Error,
			TraceEventType.Critical => LogEventLevel.Fatal,
			_ => LogEventLevel.Verbose
		};
}

internal static class AwsTraceLogging
{
	private static int _configured;

	public static void Configure()
	{
		if (Interlocked.CompareExchange(ref _configured, 1, 0) != 0)
			return;

		try
		{
			AWSConfigs.AddTraceListener("Amazon", new AwsTraceSerilogListener {
				Name = nameof(AwsTraceSerilogListener),
			});
			AWSConfigs.LoggingConfig.LogTo = LoggingOptions.SystemDiagnostics;
			AWSConfigs.LoggingConfig.LogResponses = ResponseLoggingOption.OnError;
			AWSConfigs.LoggingConfig.LogMetrics = false;
		}
		catch
		{
			Volatile.Write(ref _configured, 0);
			throw;
		}
	}
}
