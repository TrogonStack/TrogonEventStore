using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.ClusterNode;
using EventStore.Common.Utils;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Transport.Grpc.Replication;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Core.Tests.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Proto = EventStore.Replication;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class ReplicationMutualTlsTests
{
	[Test]
	public async Task trusted_client_certificate_reaches_replication_with_certificate_identity()
	{
		using var root = TestCertificates.GetRootCertificate();
		using var serverCertificate = TestCertificates.GetServerCertificate();
		using var clientCertificate = TestCertificates.GetOtherServerCertificate();
		var publisher = new CapturingPublisher();
		using var host = StartServer(serverCertificate, new X509Certificate2Collection(root), publisher);
		using var channel = CreateChannel(host, clientCertificate, new X509Certificate2Collection(root));
		var client = new Proto.Replication.ReplicationClient(channel);
		using var call = client.Replicate(deadline: DateTime.UtcNow.AddSeconds(10));

		await call.RequestStream.WriteAsync(SubscribeFrame());
		await call.RequestStream.CompleteAsync();
		Assert.That(await call.ResponseStream.MoveNext(), Is.False);
		Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaSubscriptionRequest>().Count(), Is.EqualTo(1));
		Assert.That(publisher.Messages.OfType<ReplicationMessage.ReplicaSubscriptionRequest>().Single()
			.Session.Identity.TransportIdentityKind,
			Is.EqualTo(ReplicationTransportIdentityKind.ClientCertificateSha256));
	}

	[Test]
	public async Task secure_replication_without_client_certificate_is_rejected_by_application_identity()
	{
		using var root = TestCertificates.GetRootCertificate();
		using var serverCertificate = TestCertificates.GetServerCertificate();
		var publisher = new CapturingPublisher();
		using var host = StartServer(serverCertificate, new X509Certificate2Collection(root), publisher);
		using var channel = CreateChannel(host, null, new X509Certificate2Collection(root));
		var client = new Proto.Replication.ReplicationClient(channel);
		using var call = client.Replicate(deadline: DateTime.UtcNow.AddSeconds(10));

		await call.RequestStream.WriteAsync(SubscribeFrame());
		await call.RequestStream.CompleteAsync();
		var exception = Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext());
		Assert.That(exception!.StatusCode, Is.EqualTo(StatusCode.Unauthenticated));
		Assert.That(publisher.Messages, Is.Empty);
	}

	private static IHost StartServer(
		X509Certificate2 serverCertificate,
		X509Certificate2Collection trustedRoots,
		IPublisher publisher)
	{
		var host = new HostBuilder()
			.ConfigureWebHost(webHost => webHost
				.UseKestrel(server => server.Listen(IPAddress.Loopback, 0, options =>
				{
					options.Protocols = HttpProtocols.Http2;
					options.UseHttps(Program.CreateServerOptionsSelectionCallback(
						() => serverCertificate,
						() => null,
						(certificate, chain, errors) => ClusterVNode<string>.ValidateClientCertificate(
							certificate, chain, errors, () => null, () => trustedRoots)), null);
				}))
				.ConfigureServices(services =>
				{
					services.AddGrpc();
					services.AddSingleton(new ReplicationService(
						publisher, new PassthroughAuthorizationProvider()));
				})
				.Configure(app =>
				{
					app.UseRouting();
					app.UseEndpoints(endpoints => endpoints.MapGrpcService<ReplicationService>());
				}))
			.Build();
		host.Start();
		return host;
	}

	private static GrpcChannel CreateChannel(
		IHost host,
		X509Certificate2 clientCertificate,
		X509Certificate2Collection trustedRoots)
	{
		var factory = new NodeHttpClientFactory(
			Uri.UriSchemeHttps,
			(certificate, chain, errors, names) => ClusterVNode<string>.ValidateServerCertificate(
				certificate, chain, errors, () => null, () => trustedRoots, names),
			() => clientCertificate);
		var httpClient = factory.CreateHttpClient(["localhost"]);
		var address = host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		return GrpcChannel.ForAddress(address, new GrpcChannelOptions
		{
			HttpClient = httpClient,
			DisposeHttpClient = true
		});
	}

	private static Proto.ReplicaFrame SubscribeFrame() => ReplicationGrpcCodec.ToGrpc(
		new ReplicationMessage.SubscribeReplica(
			ReplicationSubscriptionVersions.V_CURRENT,
			0,
			Guid.NewGuid(),
			[],
			new DnsEndPoint("replica.internal", 1112),
			Guid.NewGuid(),
			Guid.NewGuid(),
			true,
			Guid.NewGuid()));

	private sealed class CapturingPublisher : IPublisher
	{
		public ConcurrentQueue<Message> Messages { get; } = new();

		public void Publish(Message message) => Messages.Enqueue(message);
	}
}
