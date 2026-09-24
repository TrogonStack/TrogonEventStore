using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.ClusterNode;
using EventStore.Common.Utils;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Core.Tests.Helpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture]
public class ssl_connections_mutual_auth
{
	[TestCase(true, true, true, true, true)]
	[TestCase(true, false, true, true, false)]
	[TestCase(false, true, true, true, false)]
	[TestCase(false, false, true, true, false)]
	[TestCase(true, true, true, false, true)]
	[TestCase(true, false, true, false, true)]
	[TestCase(false, true, true, false, false)]
	[TestCase(false, false, true, false, false)]
	[TestCase(true, true, false, true, true)]
	[TestCase(true, false, false, true, false)]
	[TestCase(false, true, false, true, true)]
	[TestCase(false, false, false, true, false)]
	[TestCase(true, true, false, false, true)]
	[TestCase(true, false, false, false, true)]
	[TestCase(false, true, false, false, true)]
	[TestCase(false, false, false, false, true)]
	public async Task connection_outcome_follows_server_and_client_certificate_policy(
		bool useTrustedServerCertificate,
		bool useTrustedClientCertificate,
		bool validateServerCertificate,
		bool validateClientCertificate,
		bool shouldConnectSuccessfully)
	{
		using var rootCertificate = TestCertificates.GetRootCertificate();
		using var serverCertificate = useTrustedServerCertificate
			? TestCertificates.GetServerCertificate()
			: TestCertificates.GetUntrustedCertificate();
		using var clientCertificate = useTrustedClientCertificate
			? TestCertificates.GetOtherServerCertificate()
			: TestCertificates.GetUntrustedCertificate();
		var trustedRoots = new X509Certificate2Collection(rootCertificate);
		CertificateDelegates.ClientCertificateValidator clientValidator = validateClientCertificate
			? (certificate, chain, errors) => ClusterVNode<string>.ValidateClientCertificate(
				certificate,
				chain,
				errors,
				() => null,
				() => trustedRoots)
			: (_, _, _) => (true, null);

		using var host = StartServer(serverCertificate, clientValidator);
		var connected = await TryConnect(
			host,
			clientCertificate,
			validateServerCertificate,
			trustedRoots);

		Assert.That(connected, Is.EqualTo(shouldConnectSuccessfully));
	}

	[TestCase(true)]
	[TestCase(false)]
	public async Task client_certificate_is_optional_at_the_transport_boundary(bool validateServerCertificate)
	{
		using var rootCertificate = TestCertificates.GetRootCertificate();
		using var serverCertificate = TestCertificates.GetServerCertificate();
		var trustedRoots = new X509Certificate2Collection(rootCertificate);
		using var host = StartServer(
			serverCertificate,
			(_, _, _) => throw new AssertionException("Missing client certificates bypass node validation."));

		var connected = await TryConnect(
			host,
			clientCertificate: null,
			validateServerCertificate,
			trustedRoots);

		Assert.That(connected, Is.True);
	}

	[Test]
	public async Task server_certificate_is_required()
	{
		using var rootCertificate = TestCertificates.GetRootCertificate();
		var trustedRoots = new X509Certificate2Collection(rootCertificate);
		using var host = StartServer(
			serverCertificate: null,
			(_, _, _) => (true, null));

		var connected = await TryConnect(
			host,
			clientCertificate: null,
			validateServerCertificate: false,
			trustedRoots);

		Assert.That(connected, Is.False);
	}

	private static IHost StartServer(
		X509Certificate2 serverCertificate,
		CertificateDelegates.ClientCertificateValidator clientCertificateValidator)
	{
		var host = new HostBuilder()
			.ConfigureWebHost(webHost => webHost
				.UseKestrel(server => server.Listen(IPAddress.Loopback, 0, listenOptions =>
					listenOptions.UseHttps(Program.CreateServerOptionsSelectionCallback(
						() => serverCertificate,
						() => null,
						clientCertificateValidator), null)))
				.Configure(app => app.Run(context => context.Response.CompleteAsync())))
			.Build();
		host.Start();
		return host;
	}

	private static async Task<bool> TryConnect(
		IHost host,
		X509Certificate2 clientCertificate,
		bool validateServerCertificate,
		X509Certificate2Collection trustedRoots)
	{
		CertificateDelegates.ServerCertificateValidator serverValidator = validateServerCertificate
			? (certificate, chain, errors, otherNames) => ClusterVNode<string>.ValidateServerCertificate(
				certificate,
				chain,
				errors,
				() => null,
				() => trustedRoots,
				otherNames)
			: (_, _, _, _) => (true, null);
		var clientFactory = new NodeHttpClientFactory(
			Uri.UriSchemeHttps,
			serverValidator,
			() => clientCertificate);
		using var client = clientFactory.CreateHttpClient(["localhost"]);
		client.Timeout = TimeSpan.FromSeconds(5);
		var address = host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		using var request = new HttpRequestMessage(HttpMethod.Get, address)
		{
			Version = HttpVersion.Version20,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact,
		};

		try
		{
			using var response = await client.SendAsync(request);
			return response.IsSuccessStatusCode;
		}
		catch (HttpRequestException)
		{
			return false;
		}
	}
}
