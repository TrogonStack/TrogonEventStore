using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
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
[TestFixture(typeof(LogFormat.V3), typeof(uint))]
public class when_shutting_down_an_isolated_cluster_member<TLogFormat, TStreamId> : SpecificationWithDirectory
{
	[Test]
	public async Task completes_without_waiting_for_services_that_never_initialized()
	{
		var coreServicesStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var shutdownComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var startedServices = new HashSet<string>();
		var shutdownServices = new HashSet<string>();
		var logFormatFactory = LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory;

		var options = new ClusterVNodeOptions()
			.ReduceMemoryUsageForTests()
			.InCluster(3)
			.RunInMemory()
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
			shutdownComplete.TrySetResult(true)));

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
				Assert.Fail(
					$"Shutdown timed out. Started: [{string.Join(", ", startedServices.OrderBy(x => x))}] Shutdown: [{string.Join(", ", shutdownServices.OrderBy(x => x))}]");
			}

			await shutdownComplete.Task.WithTimeout(TimeSpan.FromSeconds(5));
		}
		finally
		{
			if (!shutdownComplete.Task.IsCompleted)
				await shutdownComplete.Task.WithTimeout(TimeSpan.FromSeconds(10));
		}
	}
}
