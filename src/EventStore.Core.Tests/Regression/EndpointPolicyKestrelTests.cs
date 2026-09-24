using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Monitoring;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core.Tests.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EventStore.Core.Tests.Regression;

[TestFixture]
public class EndpointPolicyKestrelTests
{
	[Test]
	public async Task client_and_cluster_grpc_services_are_isolated_on_distinct_tls_listeners()
	{
		using var serverCertificate = TestCertificates.GetServerCertificate();
		using var clientCertificate = TestCertificates.GetOtherServerCertificate();
		var bindings = new List<EndpointBinding>();
		var policy = new EndpointPolicy(
			bindings,
			[new(EventStore.Cluster.Gossip.Descriptor, EndpointRole.Cluster)],
			defaultRouteRole: EndpointRole.Client,
			nonIpEndpointRole: EndpointRole.Client);
		var calls = new ServiceCalls();
		using var host = new HostBuilder()
			.ConfigureWebHost(webHost => webHost
				.UseKestrel(server =>
				{
					server.Listen(IPAddress.Loopback, 0,
						listen => ConfigureTls(listen, serverCertificate, clientCertificate));
					server.Listen(IPAddress.Loopback, 0, listen =>
					{
						listen.Protocols = HttpProtocols.Http2;
						ConfigureTls(listen, serverCertificate, clientCertificate);
					});
				})
				.ConfigureServices(services =>
				{
					services.AddGrpc();
					services.AddSingleton(calls);
				})
				.Configure(app =>
				{
					app.UseEndpointPolicy(policy);
					app.UseRouting();
					app.UseEndpoints(endpoints =>
					{
						endpoints.MapGrpcService<ClientMonitoringService>();
						endpoints.MapGrpcService<ClusterGossipService>();
					});
				}))
			.Build();

		await host.StartAsync();
		var addresses = host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Select(x => new Uri(x)).ToArray();
		Assert.That(addresses, Has.Length.EqualTo(2));
		bindings.Add(new EndpointBinding(EndpointRole.Client,
			new IPEndPoint(IPAddress.Loopback, addresses[0].Port), HttpProtocols.Http1AndHttp2));
		bindings.Add(new EndpointBinding(EndpointRole.Cluster,
			new IPEndPoint(IPAddress.Loopback, addresses[1].Port), HttpProtocols.Http2));

		using var handler = new SocketsHttpHandler();
		handler.SslOptions.RemoteCertificateValidationCallback =
			(_, certificate, _, _) => certificate?.GetCertHashString() == serverCertificate.Thumbprint;
		handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
		using var httpClient = new HttpClient(handler);
		using var clientChannel = GrpcChannel.ForAddress(addresses[0],
			new GrpcChannelOptions { HttpClient = httpClient });
		using var clusterChannel = GrpcChannel.ForAddress(addresses[1],
			new GrpcChannelOptions { HttpClient = httpClient });
		var clientOnClientPort = new EventStore.Client.Monitoring.Monitoring.MonitoringClient(clientChannel);
		var clusterOnClusterPort = new EventStore.Cluster.Gossip.GossipClient(clusterChannel);

		await clientOnClientPort.ConnectionStatsAsync(new ConnectionStatsReq(), deadline: DateTime.UtcNow.AddSeconds(10));
		await clusterOnClusterPort.ReadAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10));
		var clusterOnClientPort = new EventStore.Cluster.Gossip.GossipClient(clientChannel);
		var clientOnClusterPort = new EventStore.Client.Monitoring.Monitoring.MonitoringClient(clusterChannel);
		var clusterError = Assert.ThrowsAsync<RpcException>(async () =>
			await clusterOnClientPort.ReadAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(10)));
		var clientError = Assert.ThrowsAsync<RpcException>(async () =>
			await clientOnClusterPort.ConnectionStatsAsync(new ConnectionStatsReq(), deadline: DateTime.UtcNow.AddSeconds(10)));

		Assert.Multiple(() =>
		{
			Assert.That(clusterError!.StatusCode, Is.EqualTo(StatusCode.Unimplemented));
			Assert.That(clientError!.StatusCode, Is.EqualTo(StatusCode.Unimplemented));
			Assert.That(calls.Client, Is.EqualTo(1));
			Assert.That(calls.Cluster, Is.EqualTo(1));
			Assert.That(calls.ClusterHadCertificate, Is.True);
		});
	}

	private static void ConfigureTls(ListenOptions listen, X509Certificate2 serverCertificate,
		X509Certificate2 clientCertificate)
	{
		listen.UseHttps(new HttpsConnectionAdapterOptions
		{
			ServerCertificate = serverCertificate,
			ClientCertificateMode = ClientCertificateMode.AllowCertificate,
			ClientCertificateValidation = (certificate, _, _) =>
				certificate?.GetCertHashString() == clientCertificate.Thumbprint
		});
	}

	public sealed class ClientMonitoringService(ServiceCalls calls) : EventStore.Client.Monitoring.Monitoring.MonitoringBase
	{
		public override Task<ConnectionStatsResp> ConnectionStats(ConnectionStatsReq request, ServerCallContext context)
		{
			Interlocked.Increment(ref calls.Client);
			return Task.FromResult(new ConnectionStatsResp());
		}
	}

	public sealed class ClusterGossipService(ServiceCalls calls) : EventStore.Cluster.Gossip.GossipBase
	{
		public override Task<EventStore.Cluster.ClusterInfo> Read(Empty request, ServerCallContext context)
		{
			Interlocked.Increment(ref calls.Cluster);
			calls.ClusterHadCertificate = context.GetHttpContext().Connection.ClientCertificate is not null;
			return Task.FromResult(new EventStore.Cluster.ClusterInfo());
		}
	}

	public sealed class ServiceCalls
	{
		public int Client;
		public int Cluster;
		public bool ClusterHadCertificate;
	}
}
