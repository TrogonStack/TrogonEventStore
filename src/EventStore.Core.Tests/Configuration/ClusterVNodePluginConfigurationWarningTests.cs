using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Tests.Services.Transport.Tcp;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EventStore.Core.Tests.Configuration;

[TestFixture]
[NonParallelizable]
public class ClusterVNodePluginConfigurationWarningTests
{
	private ILogger _previousLogger;
	private CollectingSink _sink;

	[SetUp]
	public void SetUp()
	{
		_previousLogger = Log.Logger;
		_sink = new CollectingSink();
		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(_sink)
			.CreateLogger();
	}

	[TearDown]
	public void TearDown()
	{
		Log.Logger = _previousLogger;
	}

	[Test]
	public async Task logs_ignored_plugins_subsection_options()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:Plugins:Authorization:DefaultPolicyType"] = "custom",
				["EventStore:Plugins:Subsystem:Enabled"] = "true",
			})
			.Build();

		await CreateNode(configuration);

		var warnings = LoggedWarnings();
		Assert.That(warnings, Has.Some.Contains("\"Plugins\" configuration subsection has been removed"));
		Assert.That(warnings, Has.Some.Contains("EventStore:Plugins:Authorization:DefaultPolicyType"));
		Assert.That(warnings, Has.Some.Contains("EventStore:Plugins:Subsystem:Enabled"));
	}

	[Test]
	public async Task does_not_log_when_plugins_subsection_is_absent()
	{
		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string>
			{
				["EventStore:Authorization:DefaultPolicyType"] = "custom",
			})
			.Build();

		await CreateNode(configuration);

		var warnings = LoggedWarnings();
		Assert.That(warnings, Has.None.Contains("\"Plugins\" configuration subsection has been removed"));
		Assert.That(warnings, Has.None.Contains("EventStore:Plugins"));
	}

	private string[] LoggedWarnings() =>
		_sink.Events
			.Where(x => x.Level == LogEventLevel.Warning)
			.Select(x => x.RenderMessage())
			.ToArray();

	private static async Task CreateNode(IConfiguration configuration)
	{
		var options = new ClusterVNodeOptions()
			.ReduceMemoryUsageForTests()
			.RunInMemory()
			.Insecure();

		var node = new ClusterVNode<string>(
			options,
			LogFormatHelper<LogFormat.V2, string>.LogFormatFactory,
			configuration: configuration);

		await node.Db.DisposeAsync();
	}

	private sealed class CollectingSink : ILogEventSink
	{
		public List<LogEvent> Events { get; } = [];

		public void Emit(LogEvent logEvent) =>
			Events.Add(logEvent);
	}
}
