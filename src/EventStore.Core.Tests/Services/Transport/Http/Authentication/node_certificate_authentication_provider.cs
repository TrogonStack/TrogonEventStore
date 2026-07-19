using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.Core.Services.Transport.Http.Authentication;
using EventStore.Core.Services.UserManagement;
using EventStore.Plugins.Authentication;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Http.Authentication;

public class TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	protected NodeCertificateAuthenticationProvider _provider;

	protected void SetUpProvider(bool allowNodeCertificateWithoutClientAuthEku = false)
	{
		_provider = new NodeCertificateAuthenticationProvider(
			() => "eventstoredb-node",
			allowNodeCertificateWithoutClientAuthEku);
	}
}

[TestFixture]
public class when_handling_a_node_certificate_without_the_client_auth_eku :
	TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	[TestCase(false, false)]
	[TestCase(true, true)]
	public async Task follows_the_explicit_compatibility_policy(bool allowNodeCertificateWithoutClientAuthEku,
		bool expectedResult)
	{
		SetUpProvider(allowNodeCertificateWithoutClientAuthEku);
		var context = new DefaultHttpContext();
		using var certificate = CreateCertificate("eventstoredb-node");
		using var presentedCertificate = await PresentCertificateOverTls(certificate);
		context.Connection.ClientCertificate = presentedCertificate;

		var result = _provider.Authenticate(context, out _);

		Assert.AreEqual(expectedResult, result);
	}

	[Test]
	public void does_not_relax_the_node_identity_policy()
	{
		SetUpProvider(allowNodeCertificateWithoutClientAuthEku: true);
		var context = new DefaultHttpContext();
		using var certificate = CreateCertificate("another-node");
		context.Connection.ClientCertificate = certificate;

		var result = _provider.Authenticate(context, out _);

		Assert.False(result);
	}

	[Test]
	public void observes_policy_changes_for_new_connections()
	{
		var allowNodeCertificateWithoutClientAuthEku = false;
		_provider = new NodeCertificateAuthenticationProvider(
			() => "eventstoredb-node",
			() => allowNodeCertificateWithoutClientAuthEku);
		using var certificate = CreateCertificate("eventstoredb-node");

		var strictContext = new DefaultHttpContext();
		strictContext.Connection.ClientCertificate = certificate;
		Assert.False(_provider.Authenticate(strictContext, out _));

		allowNodeCertificateWithoutClientAuthEku = true;
		var compatibleContext = new DefaultHttpContext();
		compatibleContext.Connection.ClientCertificate = certificate;
		Assert.True(_provider.Authenticate(compatibleContext, out _));
	}

	private static X509Certificate2 CreateCertificate(string commonName)
	{
		using var rsa = RSA.Create();
		var request = new CertificateRequest(
			$"CN={commonName}",
			rsa,
			HashAlgorithmName.SHA256,
			RSASignaturePadding.Pkcs1);
		var sanBuilder = new SubjectAlternativeNameBuilder();
		sanBuilder.AddDnsName("localhost");
		request.CertificateExtensions.Add(sanBuilder.Build());
		request.CertificateExtensions.Add(new X509KeyUsageExtension(
			X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
			critical: false));
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
			[new Oid("1.3.6.1.5.5.7.3.1")],
			critical: false));

		return request.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddMonths(-1),
			DateTimeOffset.UtcNow.AddMonths(1));
	}

	private static async Task<X509Certificate2> PresentCertificateOverTls(X509Certificate2 clientCertificate)
	{
		using var serverCertificate = CreateCertificate("localhost");
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();

		try
		{
			var serverTask = Task.Run(async () =>
			{
				using var serverClient = await listener.AcceptTcpClientAsync();
				using var serverStream = new SslStream(
					serverClient.GetStream(),
					leaveInnerStreamOpen: false,
					(_, certificate, chain, errors) =>
					{
						var result = ClusterVNode<string>.ValidateClientCertificate(
							certificate,
							chain,
							errors,
							() => null,
							() => new X509Certificate2Collection(clientCertificate));
						return result.Item1;
					});
				await serverStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
				{
					ServerCertificate = serverCertificate,
					ClientCertificateRequired = true,
					CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
					EnabledSslProtocols = SslProtocols.None
				});

				return X509CertificateLoader.LoadCertificate(
					serverStream.RemoteCertificate.Export(X509ContentType.Cert));
			});

			using var client = new TcpClient();
			await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
			using var clientStream = new SslStream(
				client.GetStream(),
				leaveInnerStreamOpen: false,
				(_, _, _, _) => true,
				(_, _, _, _, _) => clientCertificate);
			await clientStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
			{
				TargetHost = "localhost",
				ClientCertificates = new X509CertificateCollection { clientCertificate },
				CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
				EnabledSslProtocols = SslProtocols.None
			});

			return await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			listener.Stop();
		}
	}
}

[TestFixture]
public class
	when_handling_a_request_without_a_client_certificate :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private bool _authenticateResult;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		var context = new DefaultHttpContext();
		Assert.IsNull(context.Connection.ClientCertificate);
		_authenticateResult = _provider.Authenticate(context, out _);
	}

	[Test]
	public void returns_false()
	{
		Assert.IsFalse(_authenticateResult);
	}
}

[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_no_san :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		using (var rsa = RSA.Create())
		{
			var certRequest = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);
			_context.Connection.ClientCertificate = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_false()
	{
		Assert.IsFalse(_authenticateResult);
	}

	[Test]
	public void authentication_request_is_null()
	{
		Assert.IsNull(_authenticateRequest);
	}

	[Test]
	public void no_roles_are_assigned()
	{
		Assert.AreEqual(0, _context.User.Claims.Count());
	}
}

[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_an_ip_san_but_without_node_cn :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		X509Certificate2 certificate;

		using (RSA rsa = RSA.Create())
		{
			var certReq = new CertificateRequest("CN=hello", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddIpAddress(IPAddress.Loopback);
			certReq.CertificateExtensions.Add(sanBuilder.Build());
			certificate = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_context.Connection.ClientCertificate = certificate;
		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_false()
	{
		Assert.IsFalse(_authenticateResult);
	}

	[Test]
	public void authentication_request_is_null()
	{
		Assert.IsNull(_authenticateRequest);
	}

	[Test]
	public void no_roles_are_assigned()
	{
		Assert.AreEqual(0, _context.User.Claims.Count());
	}
}

[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_an_ip_san_and_node_cn :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		X509Certificate2 certificate;

		using (RSA rsa = RSA.Create())
		{
			var certReq = new CertificateRequest("CN=eventstoredb-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddIpAddress(IPAddress.Loopback);
			certReq.CertificateExtensions.Add(sanBuilder.Build());

			certReq.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

			certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
				[
					new("1.3.6.1.5.5.7.3.1"), // serverAuth
					new("1.3.6.1.5.5.7.3.2"), // clientAuth
				],
				critical: false));

			certificate = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_context.Connection.ClientCertificate = certificate;
		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_true()
	{
		Assert.IsTrue(_authenticateResult);
	}

	[Test]
	public async Task passes_authentication()
	{
		var (status, _) = await _authenticateRequest.AuthenticateAsync();
		Assert.AreEqual(HttpAuthenticationRequestStatus.Authenticated, status);
	}

	[Test]
	public async Task sets_user_to_system_user()
	{
		var (_, user) = await _authenticateRequest.AuthenticateAsync();
		Assert.AreEqual(SystemAccounts.System.Claims, user.Claims);
	}
}

[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_an_ip_san_and_node_cn_with_additional_subject_details :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		X509Certificate2 certificate;

		using (RSA rsa = RSA.Create())
		{
			var certReq = new CertificateRequest("C=UK, O=Event Store Ltd, CN=eventstoredb-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddIpAddress(IPAddress.Loopback);
			certReq.CertificateExtensions.Add(sanBuilder.Build());

			certReq.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

			certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
				[
					new("1.3.6.1.5.5.7.3.1"), // serverAuth
					new("1.3.6.1.5.5.7.3.2"), // clientAuth
				],
				critical: false));

			certificate = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_context.Connection.ClientCertificate = certificate;
		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_true()
	{
		Assert.IsTrue(_authenticateResult);
	}

	[Test]
	public async Task passes_authentication()
	{
		var (status, _) = await _authenticateRequest.AuthenticateAsync();
		Assert.AreEqual(HttpAuthenticationRequestStatus.Authenticated, status);
	}

	[Test]
	public async Task sets_user_to_system_user()
	{
		var (_, user) = await _authenticateRequest.AuthenticateAsync();
		Assert.AreEqual(SystemAccounts.System.Claims, user.Claims);
	}
}


[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_a_dns_san_but_without_node_cn :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		X509Certificate2 certificate;

		using (RSA rsa = RSA.Create())
		{
			var certReq = new CertificateRequest("CN=hello", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddDnsName("localhost");
			certReq.CertificateExtensions.Add(sanBuilder.Build());
			certificate = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_context.Connection.ClientCertificate = certificate;
		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_false()
	{
		Assert.IsFalse(_authenticateResult);
	}

	[Test]
	public void authentication_request_is_null()
	{
		Assert.IsNull(_authenticateRequest);
	}

	[Test]
	public void no_roles_are_assigned()
	{
		Assert.AreEqual(0, _context.User.Claims.Count());
	}
}

[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_a_dns_san_and_node_cn :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		X509Certificate2 certificate;

		using (RSA rsa = RSA.Create())
		{
			var certReq = new CertificateRequest("CN=eventstoredb-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddDnsName("localhost");
			certReq.CertificateExtensions.Add(sanBuilder.Build());

			certReq.CertificateExtensions.Add(new X509KeyUsageExtension(
				X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

			certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
				[
					new("1.3.6.1.5.5.7.3.1"), // serverAuth
					new("1.3.6.1.5.5.7.3.2"), // clientAuth
				],
				critical: false));

			certificate = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_context.Connection.ClientCertificate = certificate;
		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_true()
	{
		Assert.IsTrue(_authenticateResult);
	}

	[Test]
	public async Task passes_authentication()
	{
		var (status, _) = await _authenticateRequest.AuthenticateAsync();
		Assert.AreEqual(HttpAuthenticationRequestStatus.Authenticated, status);
	}

	[Test]
	public async Task sets_user_to_system_user()
	{
		var (_, user) = await _authenticateRequest.AuthenticateAsync();
		Assert.AreEqual(SystemAccounts.System.Claims, user.Claims);
	}
}

[TestFixture]
public class
	when_handling_a_request_with_a_client_certificate_having_a_non_dns_or_ip_san_with_node_cn :
		TestFixtureWithNodeCertificateHttpAuthenticationProvider
{
	private HttpAuthenticationRequest _authenticateRequest;
	private bool _authenticateResult;
	private HttpContext _context;

	[SetUp]
	public void SetUp()
	{
		SetUpProvider();
		_context = new DefaultHttpContext();
		X509Certificate2 certificate;

		using (RSA rsa = RSA.Create())
		{
			var certReq = new CertificateRequest("CN=eventstoredb-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			var sanBuilder = new SubjectAlternativeNameBuilder();
			sanBuilder.AddEmailAddress("hello@hello.org");
			sanBuilder.AddUserPrincipalName("test@test.com");
			sanBuilder.AddUri(new Uri("http://localhost"));
			certReq.CertificateExtensions.Add(sanBuilder.Build());
			certificate = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
		}

		_context.Connection.ClientCertificate = certificate;
		_authenticateResult = _provider.Authenticate(_context, out _authenticateRequest);
	}

	[Test]
	public void returns_false()
	{
		Assert.IsFalse(_authenticateResult);
	}

	[Test]
	public void authentication_request_is_null()
	{
		Assert.IsNull(_authenticateRequest);
	}

	[Test]
	public void no_roles_are_assigned()
	{
		Assert.AreEqual(0, _context.User.Claims.Count());
	}
}
