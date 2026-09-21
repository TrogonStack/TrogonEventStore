using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.ClusterNode;
using EventStore.Common.Utils;
using EventStore.Core.Tests.Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http;

[TestFixture]
public class with_intermediate_certificates : with_certificate_chain_of_length_3
{
	private IHost _host;

	[SetUp]
	public void SetUp()
	{
		var certificate = X509CertificateLoader.LoadPkcs12(_leaf.ExportToPkcs12(), null);
		_host = new HostBuilder()
			.ConfigureWebHost(webHost => webHost
				.UseKestrel(server => server.Listen(IPAddress.Loopback, 0, listenOptions =>
					listenOptions.UseHttps(Program.CreateServerOptionsSelectionCallback(
						() => certificate,
						() => new X509Certificate2Collection(_intermediate),
						(_, _, _) => (true, null)), null)))
				.Configure(app => app.Run(context => context.Response.CompleteAsync())))
			.Build();
		_host.Start();
	}

	[Test]
	public async Task server_should_send_intermediate_certificate_during_handshake()
	{
		var handler = new SocketsHttpHandler();
		var gotLeaf = false;
		var gotIntermediate = false;
		handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
		{
			gotLeaf = certificate is not null && certificate.GetCertHashString() == _leaf.GetCertHashString();
			gotIntermediate = chain is not null && chain.ChainElements.Cast<X509ChainElement>()
				.Any(element => element.Certificate.Thumbprint == _intermediate.Thumbprint);
			return true;
		};
		using var client = new HttpClient(handler);
		var address = _host.Services.GetRequiredService<IServer>()
			.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		using var request = new HttpRequestMessage(HttpMethod.Get, address)
		{
			Version = HttpVersion.Version20,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact,
		};

		using var response = await client.SendAsync(request);

		Assert.That(gotLeaf, Is.True);
		Assert.That(gotIntermediate, Is.True);
	}

	[TearDown]
	public void TearDown()
	{
		_host?.Dispose();
	}
}
