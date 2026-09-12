using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EventStore.Core.Tests.Helpers;

public static class TestCertificates
{
	private static readonly X509Certificate2 Root = CreateRootCertificate("Test Root CA");
	private static readonly X509Certificate2 Server = CreateServerCertificate("localhost", Root);
	private static readonly X509Certificate2 OtherServer = CreateServerCertificate("other-node", Root);
	private static readonly X509Certificate2 UntrustedRoot = CreateRootCertificate("Untrusted Test Root CA");
	private static readonly X509Certificate2 Untrusted = CreateServerCertificate("untrusted", UntrustedRoot);

	public static X509Certificate2 GetRootCertificate() =>
		X509CertificateLoader.LoadCertificate(Root.Export(X509ContentType.Cert));

	public static X509Certificate2 GetServerCertificate() => CloneWithPrivateKey(Server);

	public static X509Certificate2 GetOtherServerCertificate() => CloneWithPrivateKey(OtherServer);

	public static X509Certificate2 GetUntrustedCertificate() => CloneWithPrivateKey(Untrusted);

	private static X509Certificate2 CreateRootCertificate(string commonName)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest(
			$"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(
			X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
		request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

		using var certificate = request.CreateSelfSigned(
			DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
		return CloneWithPrivateKey(certificate);
	}

	private static X509Certificate2 CreateServerCertificate(string commonName, X509Certificate2 issuer)
	{
		using var key = RSA.Create(2048);
		var request = new CertificateRequest(
			$"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(
			X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
			[new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], true));
		var names = new SubjectAlternativeNameBuilder();
		names.AddDnsName(commonName);
		names.AddDnsName("localhost");
		names.AddIpAddress(IPAddress.Loopback);
		request.CertificateExtensions.Add(names.Build());

		var serial = RandomNumberGenerator.GetBytes(16);
		using var certificate = request.Create(
			issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), serial);
		using var certificateWithKey = certificate.CopyWithPrivateKey(key);
		return CloneWithPrivateKey(certificateWithKey);
	}

	private static X509Certificate2 CloneWithPrivateKey(X509Certificate2 certificate) =>
		X509CertificateLoader.LoadPkcs12(
			certificate.Export(X509ContentType.Pkcs12), string.Empty, X509KeyStorageFlags.Exportable);
}
