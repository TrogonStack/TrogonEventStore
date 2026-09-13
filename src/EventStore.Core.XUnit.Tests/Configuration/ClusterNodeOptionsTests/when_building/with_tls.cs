using System;
using System.IO;
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
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.XUnit.Tests.Configuration.ClusterNodeOptionsTests.when_building;

[Category("LongRunning")]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_tls_enabled_and_using_a_security_certificate_from_file<TLogFormat, TStreamId> : SingleNodeScenario<TLogFormat, TStreamId>
{
	protected override ClusterVNodeOptions WithOptions(ClusterVNodeOptions options)
	{
		return options with
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

	private string GetCertificatePath()
	{
		var filePath = Path.Combine(PathName, $"cert-{Guid.NewGuid()}.p12");
		var cert = TestCertificates.GetUntrustedCertificate();

		using var fileStream = File.Create(filePath);
		fileStream.Write(cert.ExportToPkcs12());

		return filePath;
	}
}

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_tls_enabled_and_using_a_security_certificate<TLogFormat, TStreamId> : SingleNodeScenario<TLogFormat, TStreamId>
{
	private readonly X509Certificate2 _certificate = TestCertificates.GetServerCertificate();

	protected override ClusterVNodeOptions WithOptions(ClusterVNodeOptions options)
	{
		return options.Secure(new X509Certificate2Collection(TestCertificates.GetRootCertificate()), _certificate);
	}

	[Test]
	public void should_set_certificate()
	{
		Assert.AreNotEqual("n/a", _options.Certificate == null ? "n/a" : _options.Certificate.ToString());
	}
}

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class with_tls_enabled_and_no_certificates<TLogFormat, TStreamId> : SpecificationWithDirectoryPerTestFixture
{
	private ClusterVNodeOptions _options;
	private Exception _caughtException;

	[OneTimeSetUp]
	public void SetUp()
	{
		_options = new ClusterVNodeOptions()
			.ReduceMemoryUsageForTests()
			.RunOnDisk(PathName);
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
