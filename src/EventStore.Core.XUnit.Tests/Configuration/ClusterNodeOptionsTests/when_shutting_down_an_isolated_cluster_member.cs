using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Authorization;
using EventStore.Core.Authorization.AuthorizationPolicies;
using EventStore.Core.Bus;
using EventStore.Core.Certificates;
using EventStore.Core.LogAbstraction;
using EventStore.Core.Messages;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Services.Transport.Tcp;
using NUnit.Framework;

namespace EventStore.Core.XUnit.Tests.Configuration.ClusterNodeOptionsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class when_shutting_down_an_isolated_cluster_member<TLogFormat, TStreamId> : SpecificationWithDirectory
{
	[Test]
	public async Task completes_without_waiting_for_services_that_never_initialized()
	{
		var coreServicesStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var shutdownComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var startedServices = new HashSet<string>();
		var shutdownServices = new HashSet<string>();
		var shutdownSignals = 0;
		var logFormatFactory = LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory;

		static string[] Snapshot(HashSet<string> services)
		{
			lock (services)
			{
				return services.OrderBy(x => x).ToArray();
			}
		}

		var options = new ClusterVNodeOptions()
			.ReduceMemoryUsageForTests()
			.InCluster(3)
			.RunOnDisk(PathName)
			.Secure(new X509Certificate2Collection(ssl_connections.GetRootCertificate()),
				ssl_connections.GetServerCertificate());

		var node = new ClusterVNode<TStreamId>(options, logFormatFactory,
			new AuthenticationProviderFactory(c =>
				new InternalAuthenticationProviderFactory(c, options.DefaultUser)),
			new AuthorizationProviderFactory(c => new InternalAuthorizationProviderFactory(
				new StaticAuthorizationPolicyRegistry([new LegacyPolicySelectorFactory(
					options.Application.AllowAnonymousEndpointAccess,
					options.Application.AllowAnonymousStreamAccess,
					options.Application.OverrideAnonymousEndpointAccessForGossip).Create(c.MainQueue)]))),
			certificateProvider: new OptionsCertificateProvider());

		node.MainBus.Subscribe(new AdHocHandler<SystemMessage.ServiceInitialized>(message =>
		{
			lock (startedServices)
			{
				if (!startedServices.Add(message.ServiceName))
					return;

				if (startedServices.Contains("StorageChaser")
				    && startedServices.Contains("StorageReader")
				    && startedServices.Contains("StorageWriter"))
				{
					coreServicesStarted.TrySetResult(true);
				}
			}
		}));

		node.MainBus.Subscribe(new AdHocHandler<SystemMessage.BecomeShutdown>(_ =>
		{
			Interlocked.Increment(ref shutdownSignals);
			shutdownComplete.TrySetResult(true);
		}));

		node.MainBus.Subscribe(new AdHocHandler<SystemMessage.ServiceShutdown>(message =>
		{
			lock (shutdownServices)
			{
				shutdownServices.Add(message.ServiceName);
			}
		}));

		try
		{
			await node.StartAsync(waitUntilReady: false);
			await coreServicesStarted.Task.WithTimeout(TimeSpan.FromSeconds(30));
			try
			{
				await node.StopAsync(TimeSpan.FromSeconds(2));
			}
			catch (TimeoutException)
			{
				await shutdownComplete.Task.WithTimeout(TimeSpan.FromSeconds(10));
				var startedServicesSnapshot = Snapshot(startedServices);
				var shutdownServicesSnapshot = Snapshot(shutdownServices);
				Assert.Fail(
					$"Shutdown timed out. Started: [{string.Join(", ", startedServicesSnapshot)}] Shutdown: [{string.Join(", ", shutdownServicesSnapshot)}]");
			}

			await shutdownComplete.Task.WithTimeout(TimeSpan.FromSeconds(5));

			var completedStartedServices = Snapshot(startedServices);
			if (completedStartedServices.Contains("Leader Replication Service"))
			{
				AssertEx.IsOrBecomesTrue(
					() => Snapshot(shutdownServices).Contains("Leader Replication Service"),
					TimeSpan.FromSeconds(1),
					"Timed out waiting for the leader replication service to report shutdown");
			}

			Assert.That(Volatile.Read(ref shutdownSignals), Is.EqualTo(1));
		}
		finally
		{
			if (!shutdownComplete.Task.IsCompleted)
				await shutdownComplete.Task.WithTimeout(TimeSpan.FromSeconds(10));
		}
	}
}
