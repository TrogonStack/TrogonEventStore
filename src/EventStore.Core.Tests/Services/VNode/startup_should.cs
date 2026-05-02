using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Authorization;
using EventStore.Core.Authorization.AuthorizationPolicies;
using EventStore.Core.Certificates;
using EventStore.Core.Configuration.Sources;
using EventStore.Core.Services.Monitoring;
using EventStore.Core.Tests.Helpers;
using EventStore.Core.Tests.Services.Transport.Tcp;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.VNode;

[TestFixture]
public class startup_should : SpecificationWithDirectory
{
	[Test]
	public async Task propagate_cancellation_into_startup_tasks()
	{
		var startupTaskStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var blockingStartupTask = new BlockingStartupTask(startupTaskStarted);
		var ip = IPAddress.Loopback;
		var tcpEndPoint = new IPEndPoint(ip, PortsHelper.GetAvailablePort(ip));
		var internalEndPoint = new IPEndPoint(ip, PortsHelper.GetAvailablePort(ip));
		var httpEndPoint = new IPEndPoint(ip, PortsHelper.GetAvailablePort(ip));

		var options = new ClusterVNodeOptions
		{
			Application = new()
			{
				AllowAnonymousEndpointAccess = true,
				AllowAnonymousStreamAccess = true,
				StatsPeriodSec = 60 * 60,
				WorkerThreads = 1
			},
			Interface = new()
			{
				ReplicationHeartbeatInterval = 10_000,
				ReplicationHeartbeatTimeout = 10_000,
				EnableAtomPubOverHttp = true
			},
			Cluster = new()
			{
				DiscoverViaDns = false,
				StreamInfoCacheCapacity = 10_000
			},
			Database = new()
			{
				ChunkSize = MiniNode.ChunkSize,
				ChunksCacheSize = MiniNode.CachedChunkSize,
				SkipDbVerify = true,
				StatsStorage = StatsStorage.None,
				DisableScavengeMerging = true,
				CommitTimeoutMs = 10_000,
				PrepareTimeoutMs = 10_000,
				StreamExistenceFilterSize = 10_000
			},
			LoadedOptions = ClusterVNodeOptions.GetLoadedOptions(new ConfigurationBuilder()
				.AddEventStoreDefaultValues()
				.Build()),
		}.Secure(new X509Certificate2Collection(ssl_connections.GetRootCertificate()),
				ssl_connections.GetServerCertificate())
			.WithReplicationEndpointOn(internalEndPoint)
			.WithExternalTcpOn(tcpEndPoint)
			.WithNodeEndpointOn(httpEndPoint)
			.RunOnDisk(System.IO.Path.Combine(PathName, "db"));

		var configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new KeyValuePair<string, string>[] {
				new("EventStore:TcpUnitTestPlugin:NodeTcpPort", tcpEndPoint.Port.ToString()),
				new("EventStore:TcpUnitTestPlugin:NodeHeartbeatInterval", "10000"),
				new("EventStore:TcpUnitTestPlugin:NodeHeartbeatTimeout", "10000"),
				new("EventStore:TcpUnitTestPlugin:Insecure", options.Application.Insecure.ToString()),
			})
			.Build();

		var node = new ClusterVNode<string>(
			options,
			LogFormatHelper<LogFormat.V2, string>.LogFormatFactory,
			new AuthenticationProviderFactory(components =>
				new InternalAuthenticationProviderFactory(components, options.DefaultUser)),
			new AuthorizationProviderFactory(components =>
				new InternalAuthorizationProviderFactory(
					new StaticAuthorizationPolicyRegistry([new LegacyPolicySelectorFactory(
						options.Application.AllowAnonymousEndpointAccess,
						options.Application.AllowAnonymousStreamAccess,
						options.Application.OverrideAnonymousEndpointAccessForGossip).Create(components.MainQueue)]))),
			certificateProvider: new OptionsCertificateProvider(),
			configuration: configuration,
			configureAdditionalNodeServices: services =>
				services.Decorate<IReadOnlyList<IClusterVNodeStartupTask>>(
					startupTasks => [..startupTasks, blockingStartupTask]));

		using var host = new TestServer(
			new Microsoft.AspNetCore.Hosting.WebHostBuilder().UseStartup(node.Startup));

		using var cancellationTokenSource = new CancellationTokenSource();
		var startTask = node.StartAsync(false, cancellationTokenSource.Token);

		await startupTaskStarted.Task.WithTimeout(TimeSpan.FromSeconds(5));
		await cancellationTokenSource.CancelAsync();

		Assert.That(async () => await startTask, Throws.InstanceOf<OperationCanceledException>());
	}

	private sealed class BlockingStartupTask(TaskCompletionSource<bool> started) : IClusterVNodeStartupTask
	{
		public async Task Run(CancellationToken token = default)
		{
			started.TrySetResult(true);
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
		}
	}
}
