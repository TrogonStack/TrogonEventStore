using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EventStore.Core.Services.Transport.Grpc.Forwarding;

internal enum ForwardingTransportIdentityKind
{
	ClientCertificateSha256,
	InsecureSystem
}

internal sealed record ForwardingSessionIdentity
{
	private const string InsecureSystemIdentity = "system";

	private ForwardingSessionIdentity(
		Guid followerInstanceId,
		ForwardingTransportIdentityKind transportIdentityKind,
		string transportIdentity)
	{
		ArgumentOutOfRangeException.ThrowIfEqual(followerInstanceId, Guid.Empty);
		ArgumentException.ThrowIfNullOrWhiteSpace(transportIdentity);

		FollowerInstanceId = followerInstanceId;
		TransportIdentityKind = transportIdentityKind;
		TransportIdentity = transportIdentity;
	}

	public Guid FollowerInstanceId { get; }
	public ForwardingTransportIdentityKind TransportIdentityKind { get; }
	public string TransportIdentity { get; }

	public static ForwardingSessionIdentity ForClientCertificate(
		Guid followerInstanceId,
		X509Certificate2 clientCertificate)
	{
		ArgumentNullException.ThrowIfNull(clientCertificate);
		return new ForwardingSessionIdentity(
			followerInstanceId,
			ForwardingTransportIdentityKind.ClientCertificateSha256,
			clientCertificate.GetCertHashString(HashAlgorithmName.SHA256));
	}

	public static ForwardingSessionIdentity ForInsecureSystem(Guid followerInstanceId) =>
		new(followerInstanceId, ForwardingTransportIdentityKind.InsecureSystem, InsecureSystemIdentity);
}
