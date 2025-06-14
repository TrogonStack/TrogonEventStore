using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.Tests.Helpers;
using EventStore.Plugins.Subsystems;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.VNode;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class ShutdownServiceWithMiniNodeTests<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private readonly CancellationTokenSource _cancellationTokenSource = new();
	private readonly TaskCompletionSource _taskCompletionSource = new();
	private MiniNode<TLogFormat, TStreamId> _node;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();
		_node = new MiniNode<TLogFormat, TStreamId>(PathName, subsystems: [new FakePlugin(_taskCompletionSource)]);
		await _node.Start();
	}

	[Test]
	public async Task Shutdown_WithMiniNode_ShouldCompleteGracefully()
	{
		await using var _ = _cancellationTokenSource.Token.Register(() =>
			_taskCompletionSource.TrySetCanceled(_cancellationTokenSource.Token));
		_cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(15));
		await _node.Shutdown();
		await _taskCompletionSource.Task;
	}

	private class FakePlugin(TaskCompletionSource source) : ISubsystem
	{
		private IPublisher _publisher;
		public void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }

		public void ConfigureApplication(IApplicationBuilder builder, IConfiguration configuration)
		{
			_publisher = builder.ApplicationServices.GetRequiredService<IPublisher>();
			_publisher.Publish(new SystemMessage.RegisterForGracefulTermination("foobar", () =>
			{
				var envelope = new CallbackEnvelope(msg =>
				{
					if (msg is not ClientMessage.ReadStreamEventsForwardCompleted resp)
						return;

					if (resp.Result != ReadStreamResult.NoStream)
						return;

					source.TrySetResult();
					_publisher.Publish(new SystemMessage.ComponentTerminated("foobar"));
				});

				_publisher.Publish(
					new ClientMessage.ReadStreamEventsForward(
						Guid.NewGuid(),
						Guid.NewGuid(),
						envelope,
						"foobar",
						0,
						1,
						false,
						false,
						null,
						SystemAccounts.System,
						true));
			}));
		}

		public string Name => "foobar";
		public string DiagnosticsName => "foobar";
		public KeyValuePair<string, object>[] DiagnosticsTags { get; }
		public string Version => "version";
		public bool Enabled => true;
		public string LicensePublicKey { get; }
		public Task Start() => Task.CompletedTask;

		public Task Stop() => Task.CompletedTask;
	}
}
