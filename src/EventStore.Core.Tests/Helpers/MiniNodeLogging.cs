using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Core.Enrichers;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Sinks.InMemory;

namespace EventStore.Core.Tests.Helpers;

public static class MiniNodeLogging
{
	// A shared sink captures cluster callbacks that can write outside the test context.
	private static readonly object SinkLock = new();
	private static InMemorySink _inMemorySink = new();

	private static ILogger _logger = CreateLogger(_inMemorySink);

	private static ILogger CreateLogger(InMemorySink sink) => new LoggerConfiguration()
		.Enrich.WithProcessId()
		.Enrich.WithThreadId()
		.Enrich.FromLogContext()
		.MinimumLevel.Debug()
		.WriteTo.Sink(sink)
		.CreateLogger();

	private const string Template =
		"MiniNode: [{ProcessId,5},{ThreadId,2},{Timestamp:HH:mm:ss.fff},{Level:u3}] {Message}{NewLine}{Exception}";

	public static void Setup()
	{
		lock (SinkLock)
		{
			_inMemorySink.Dispose();
			_inMemorySink = new InMemorySink();
			_logger = CreateLogger(_inMemorySink);
			Serilog.Log.Logger = _logger;
		}
	}

	public static void WriteLogs()
	{
		TestContext.Error.WriteLine("MiniNode: Start writing logs...");

		var sb = new StringBuilder(256);
		var f = new MessageTemplateTextFormatter(Template);
		using var tw = new StringWriter(sb);
		LogEvent[] logEvents;

		lock (SinkLock)
		{
			using var snapshot = _inMemorySink.Snapshot();
			logEvents = snapshot.LogEvents.ToArray();
		}

		foreach (var e in logEvents)
		{
			sb.Clear();
			f.Format(e, tw);
			TestContext.Error.Write(sb.ToString());
		}

		TestContext.Error.WriteLine("MiniNode: Writing logs done.");
	}

	public static void Clear()
	{
		lock (SinkLock)
		{
			Serilog.Log.Logger = Logger.None;
			_logger = Logger.None;
			_inMemorySink.Dispose();
			_inMemorySink = new InMemorySink();
		}
	}
}
