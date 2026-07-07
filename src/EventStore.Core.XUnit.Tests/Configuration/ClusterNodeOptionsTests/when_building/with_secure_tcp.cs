using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Authentication;
using EventStore.Core.Authentication.DelegatedAuthentication;
using EventStore.Core.Authentication.InternalAuthentication;
using EventStore.Core.Authorization;
using EventStore.Core.Authorization.AuthorizationPolicies;
using EventStore.Core.Certificates;
using EventStore.Core.Tests;
using EventStore.Core.Tests.Services.Transport.Tcp;
using NUnit.Framework;

namespace EventStore.Core.XUnit.Tests.Configuration.ClusterNodeOptionsTests.when_building;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_ssl_enabled_and_using_a_security_certificate_from_file<TLogFormat, TStreamId> : SingleNodeScenario<TLogFormat, TStreamId>
{
	private readonly IPEndPoint _internalSecTcp = new(IPAddress.Parse("127.0.1.15"), 1114);
	private readonly IPEndPoint _externalSecTcp = new(IPAddress.Parse("127.0.1.15"), 1115);

	protected override ClusterVNodeOptions WithOptions(ClusterVNodeOptions options)
	{

		return options.WithReplicationEndpointOn(_internalSecTcp).WithExternalTcpOn(_externalSecTcp) with
		{
			CertificateFile = new()
			{
				CertificateFile = GetCertificatePath(),
				CertificatePrivateKeyFile = string.Empty,
				CertificatePassword = "password"
			}
		};
	}

	[Test]
	public void should_set_certificate()
	{
		Assert.AreNotEqual("n/a", _options.Certificate == null ? "n/a" : _options.Certificate.ToString());
	}

	[Test]
	public void should_set_internal_secure_tcp_endpoint()
	{
		Assert.AreEqual(_internalSecTcp, _node.NodeInfo.InternalSecureTcp);
	}

	private string GetCertificatePath()
	{
		var filePath = Path.Combine(PathName, $"cert-{Guid.NewGuid()}.p12");
		var cert = ssl_connections.GetUntrustedCertificate();

		using var fileStream = File.Create(filePath);
		fileStream.Write(cert.ExportToPkcs12());

		return filePath;
	}
}

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_ssl_enabled_and_using_a_security_certificate<TLogFormat, TStreamId> : SingleNodeScenario<TLogFormat, TStreamId>
{
	private readonly IPEndPoint _internalSecTcp = new(IPAddress.Parse("127.0.1.15"), 1114);
	private readonly IPEndPoint _externalSecTcp = new(IPAddress.Parse("127.0.1.15"), 1115);
	private readonly X509Certificate2 _certificate = ssl_connections.GetServerCertificate();

	protected override ClusterVNodeOptions WithOptions(ClusterVNodeOptions options)
	{
		return options
			.WithReplicationEndpointOn(_internalSecTcp)
			.WithExternalTcpOn(_externalSecTcp)
			.Secure(new X509Certificate2Collection(ssl_connections.GetRootCertificate()), _certificate);
	}

	[Test]
	public void should_set_certificate()
	{
		Assert.AreNotEqual("n/a", _options.Certificate == null ? "n/a" : _options.Certificate.ToString());
	}

	[Test]
	public void should_set_internal_secure_tcp_endpoint()
	{
		Assert.AreEqual(_internalSecTcp, _node.NodeInfo.InternalSecureTcp);
	}
}

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_secure_tcp_endpoints_and_no_certificates<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private ClusterVNodeOptions _options;
	private Exception _caughtException;

	[OneTimeSetUp]
	public void SetUp()
	{
		var baseIpAddress = IPAddress.Parse("127.0.1.15");
		var internalSecTcp = new IPEndPoint(baseIpAddress, 1114);
		var externalSecTcp = new IPEndPoint(baseIpAddress, 1115);
		_options = new ClusterVNodeOptions()
			.ReduceMemoryUsageForTests()
			.RunOnDisk(PathName)
			.WithReplicationEndpointOn(internalSecTcp)
			.WithExternalTcpOn(externalSecTcp);
		try
		{
			_ = new ClusterVNode<TStreamId>(_options, LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory,
				certificateProvider: new OptionsCertificateProvider());
		}
		catch (Exception ex)
		{
			_caughtException = ex;
		}
	}

	[Test]
	public void should_throw_an_exception()
	{
		Assert.IsNotNull(_caughtException);
	}
}

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_tls_disabled_and_auth_enabled<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private ClusterVNode<TStreamId> _node;
	private ClusterVNodeOptions _options;

	[OneTimeSetUp]
	public override async Task TestFixtureSetUp()
	{
		await base.TestFixtureSetUp();

		_options = new ClusterVNodeOptions()
			.ReduceMemoryUsageForTests()
			.RunOnDisk(PathName);
		_options = _options with
		{
			Application = _options.Application with
			{
				DisableTls = true,
			},
		};

		_node = new ClusterVNode<TStreamId>(_options, LogFormatHelper<TLogFormat, TStreamId>.LogFormatFactory,
			new AuthenticationProviderFactory(c =>
				new InternalAuthenticationProviderFactory(c, _options.DefaultUser)),
			new AuthorizationProviderFactory(c => new InternalAuthorizationProviderFactory(
				new StaticAuthorizationPolicyRegistry([new LegacyPolicySelectorFactory(
					_options.Application.AllowAnonymousEndpointAccess,
					_options.Application.AllowAnonymousStreamAccess,
					_options.Application.OverrideAnonymousEndpointAccessForGossip).Create(c.MainQueue)]))),
			certificateProvider: new OptionsCertificateProvider());
	}

	[Test]
	public void should_not_require_certificates()
	{
		Assert.IsNotNull(_node);
	}

	[Test]
	public void should_disable_transport_tls()
	{
		Assert.IsTrue(_node.DisableHttps);
		Assert.IsTrue(_options.Application.TlsDisabled());
		Assert.AreEqual(new IPEndPoint(IPAddress.Loopback, 1112), _node.NodeInfo.InternalTcp);
	}

	[Test]
	public void should_keep_auth_enabled()
	{
		Assert.IsFalse(_options.Application.AuthDisabled());
		Assert.IsInstanceOf<DelegatedAuthenticationProvider>(_node.AuthenticationProvider);
		var delegatedAuthenticationProvider = (DelegatedAuthenticationProvider)_node.AuthenticationProvider;
		Assert.IsInstanceOf<InternalAuthenticationProvider>(delegatedAuthenticationProvider.Inner);
	}
}
