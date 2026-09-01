using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EventStore.Core.Services.Replication;

public enum ReplicationTransportIdentityKind
{
	ClientCertificateSha256,
	InsecureSystem
}

public sealed record ReplicationSessionIdentity
{
	private const string InsecureSystemIdentity = "system";

	private ReplicationSessionIdentity(
		Guid replicaInstanceId,
		ReplicationTransportIdentityKind transportIdentityKind,
		string transportIdentity)
	{
		ArgumentOutOfRangeException.ThrowIfEqual(replicaInstanceId, Guid.Empty);
		ArgumentException.ThrowIfNullOrWhiteSpace(transportIdentity);

		ReplicaInstanceId = replicaInstanceId;
		TransportIdentityKind = transportIdentityKind;
		TransportIdentity = transportIdentity;
	}

	public Guid ReplicaInstanceId { get; }
	public ReplicationTransportIdentityKind TransportIdentityKind { get; }
	public string TransportIdentity { get; }

	public static ReplicationSessionIdentity ForClientCertificate(
		Guid replicaInstanceId,
		X509Certificate2 clientCertificate)
	{
		ArgumentNullException.ThrowIfNull(clientCertificate);
		return new ReplicationSessionIdentity(
			replicaInstanceId,
			ReplicationTransportIdentityKind.ClientCertificateSha256,
			clientCertificate.GetCertHashString(HashAlgorithmName.SHA256));
	}

	public static ReplicationSessionIdentity ForInsecureSystem(Guid replicaInstanceId) =>
		new(replicaInstanceId, ReplicationTransportIdentityKind.InsecureSystem, InsecureSystemIdentity);
}
