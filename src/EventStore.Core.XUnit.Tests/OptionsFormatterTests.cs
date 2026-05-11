using System;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace EventStore.Core.XUnit.Tests;

[Collection("SerilogLoggingTests")]
public class OptionsFormatterTests : IDisposable
{
	private readonly ILogger _previousLogger = Log.Logger;
	private readonly CollectingSink _sink = new();

	public OptionsFormatterTests()
	{
		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(_sink)
			.CreateLogger();
	}

	[Fact]
	public void logs_named_configuration_as_json()
	{
		OptionsFormatter.LogConfig("Archive", new SampleOptions
		{
			Mode = SampleMode.FileSystem,
			Path = "/tmp/archive",
		});

		var logEvent = Assert.Single(_sink.Events);
		var message = logEvent.RenderMessage();
		Assert.Contains("Archive Configuration:", message, StringComparison.Ordinal);
		Assert.Contains("\"Mode\": \"FileSystem\"", message, StringComparison.Ordinal);
		Assert.DoesNotContain("\"Ignored\"", message, StringComparison.Ordinal);
	}

	[Fact]
	public void redacts_sensitive_values()
	{
		OptionsFormatter.LogConfig("Archive", new SampleOptions
		{
			Mode = SampleMode.FileSystem,
			Path = "/tmp/archive",
			Password = "secret-password",
			Nested = new NestedOptions
			{
				AccessKey = "abc123",
				ApiToken = "token-value",
			}
		});

		var logEvent = Assert.Single(_sink.Events);
		var message = logEvent.RenderMessage();
		Assert.DoesNotContain("secret-password", message, StringComparison.Ordinal);
		Assert.DoesNotContain("abc123", message, StringComparison.Ordinal);
		Assert.DoesNotContain("token-value", message, StringComparison.Ordinal);
		Assert.Contains("\"Password\": \"***REDACTED***\"", message, StringComparison.Ordinal);
		Assert.Contains("\"AccessKey\": \"***REDACTED***\"", message, StringComparison.Ordinal);
		Assert.Contains("\"ApiToken\": \"***REDACTED***\"", message, StringComparison.Ordinal);
	}

	public void Dispose()
	{
		Log.Logger = _previousLogger;
	}

	private sealed class CollectingSink : ILogEventSink
	{
		public List<LogEvent> Events { get; } = [];

		public void Emit(LogEvent logEvent) =>
			Events.Add(logEvent);
	}

	private sealed class SampleOptions
	{
		public SampleMode Mode { get; init; }
		public string Path { get; init; } = "";
		public string Ignored { get; init; }
		public string Password { get; init; }
		public NestedOptions Nested { get; init; }
	}

	private sealed class NestedOptions
	{
		public string AccessKey { get; init; }
		public string ApiToken { get; init; }
	}

	private enum SampleMode
	{
		FileSystem,
	}
}
