using System;
using System.Collections.Generic;
using System.Diagnostics;
using Amazon.Runtime;
using EventStore.Core.Services.Archive.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Archive.Storage;

public class AwsTraceSerilogListenerTests
{
	[Fact]
	public void logs_formatted_aws_trace_messages()
	{
		var sink = new CollectingSink();
		var logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(sink)
			.CreateLogger();
		var sut = new AwsTraceSerilogListener(logger);
		var exception = new InvalidOperationException("boom");

		sut.TraceData(null, "Amazon", TraceEventType.Warning, 0, new FakeLogMessage("Problem {Value}", 42), exception);

		var logEvent = Assert.Single(sink.Events);
		Assert.Equal(LogEventLevel.Warning, logEvent.Level);
		Assert.Equal("Problem 42", logEvent.RenderMessage());
		Assert.Same(exception, logEvent.Exception);
	}

	[Fact]
	public void logs_plain_trace_messages()
	{
		var sink = new CollectingSink();
		var logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(sink)
			.CreateLogger();
		var sut = new AwsTraceSerilogListener(logger);

		sut.TraceData(null, "Amazon", TraceEventType.Error, 0, "plain message");

		var logEvent = Assert.Single(sink.Events);
		Assert.Equal(LogEventLevel.Error, logEvent.Level);
		Assert.Equal("plain message", logEvent.RenderMessage());
	}

	private sealed class CollectingSink : ILogEventSink
	{
		public List<LogEvent> Events { get; } = new();

		public void Emit(LogEvent logEvent) =>
			Events.Add(logEvent);
	}

	private sealed class FakeLogMessage(string format, params object[] args) : ILogMessage
	{
		public string Format => format;
		public object[] Args => args;
		public IFormatProvider Provider => null;
	}
}
