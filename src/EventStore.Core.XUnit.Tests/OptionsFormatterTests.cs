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
	}

	private enum SampleMode
	{
		FileSystem,
	}
}
