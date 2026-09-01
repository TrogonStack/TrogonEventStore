using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Cluster;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Metrics;
using EventStore.Core.Services.Replication;
using EventStore.Core.Services.Transport.Http.NodeHttpClientFactory;
using EventStore.Core.Tests.Fakes;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;
using Proto = EventStore.Replication;

namespace EventStore.Core.Tests.Services.Transport.Grpc.Replication;

[TestFixture]
public class GrpcReplicaServiceFactoryTests
{
	[Test]
	public void replication_client_uses_node_certificate_names_and_owns_its_http_client()
	{
		var nodeHttpClientFactory = new RecordingNodeHttpClientFactory();
		var factory = new ReplicationGrpcClientFactory(Uri.UriSchemeHttps, nodeHttpClientFactory);
		var leaderEndPoint = new IPWithClusterDnsEndPoint(
			IPAddress.Loopback,
			"cluster.internal",
			2113);

		var client = factory.Create(leaderEndPoint);

		Assert.That(nodeHttpClientFactory.AdditionalCertificateNames, Is.EqualTo(new[] { "cluster.internal" }));
		client.Dispose();
		Assert.That(nodeHttpClientFactory.Handler.Disposed, Is.True);
	}

	[Test]
	public async Task cache_eviction_does_not_dispose_active_replication_client()
	{
		var cacheLifetime = TimeSpan.FromMilliseconds(25);
		var publisher = new FakePublisher();
		var nodeHttpClientFactory = new NodeHttpClientFactory(
			Uri.UriSchemeHttp,
			nodeCertificateValidator: null,
			clientCertificateSelector: null);
		var cache = new EventStoreClusterClientCache(
			publisher,
			(endpoint, bus) => new EventStoreClusterClient(
				bus,
				Uri.UriSchemeHttp,
				endpoint,
				nodeHttpClientFactory,
				clusterDns: null,
				new DurationTracker.NoOp(),
				new DurationTracker.NoOp()),
			cacheCleaningInterval: TimeSpan.FromMinutes(1),
			oldCacheItemThreshold: cacheLifetime);
		var leaderEndPoint = new IPEndPoint(IPAddress.Loopback, 2113);
		var cachedClient = cache.Get(leaderEndPoint);
		var replicationClientFactory = new TrackingReplicationGrpcClientFactory();
		var factory = new GrpcReplicaServiceFactory(
			replicationClientFactory,
			new StubReplicaSubscriptionDataSource(),
			Guid.NewGuid(),
			ReplicaPromotability.Promotable);
		var service = factory.Create(
			publisher,
			new GrpcReplicaConnectionEndpoints(
				leaderEndPoint,
				new IPEndPoint(IPAddress.Loopback, 2114)));
		_ = service.Start();

		await Task.Delay(cacheLifetime + TimeSpan.FromMilliseconds(50));
		cache.Handle(new ClusterClientMessage.CleanCache());

		Assert.Multiple(() =>
		{
			Assert.That(SpinWait.SpinUntil(() => cachedClient.Disposed, TimeSpan.FromSeconds(5)), Is.True);
			Assert.That(replicationClientFactory.Client.Disposed, Is.False);
			Assert.That(service.Task.IsCompleted, Is.False);
		});

		await service.StopAsync();
		Assert.That(replicationClientFactory.Client.Disposed, Is.True);
	}

	private sealed class TrackingReplicationGrpcClientFactory : IReplicationGrpcClientFactory
	{
		public TrackingReplicationGrpcClient Client { get; } = new();

		public IReplicationGrpcClient Create(EndPoint leaderEndPoint) => Client;
	}

	private sealed class RecordingNodeHttpClientFactory : INodeHttpClientFactory
	{
		public RecordingHttpMessageHandler Handler { get; } = new();
		public string[] AdditionalCertificateNames { get; private set; }

		public HttpClient CreateHttpClient(string[] additionalCertificateNames)
		{
			AdditionalCertificateNames = additionalCertificateNames;
			return new HttpClient(Handler);
		}
	}

	private sealed class RecordingHttpMessageHandler : HttpMessageHandler
	{
		public bool Disposed { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			Disposed = true;
			base.Dispose(disposing);
		}
	}

	private sealed class TrackingReplicationGrpcClient : IReplicationGrpcClient
	{
		public bool Disposed { get; private set; }

		public IReplicationGrpcCall Replicate(CancellationToken cancellationToken) =>
			new BlockingReplicationGrpcCall();

		public void Dispose() => Disposed = true;
	}

	private sealed class BlockingReplicationGrpcCall : IReplicationGrpcCall
	{
		public Task WriteAsync(Proto.ReplicaFrame frame) => Task.CompletedTask;

		public Task CompleteRequestAsync() => Task.CompletedTask;

		public async IAsyncEnumerable<Proto.LeaderFrame> ReadAllAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			yield break;
		}

		public void Dispose()
		{
		}
	}

	private sealed class StubReplicaSubscriptionDataSource : IReplicaSubscriptionDataSource
	{
		public long ReadNonFlushed() => 0;

		public ValueTask<Guid> GetCurrentChunkIdAsync(
			long logPosition,
			CancellationToken cancellationToken) => ValueTask.FromResult(Guid.Empty);

		public ValueTask<IReadOnlyList<EpochRecord>> GetLastEpochsAsync(
			int maxCount,
			CancellationToken cancellationToken) =>
			ValueTask.FromResult<IReadOnlyList<EpochRecord>>(Array.Empty<EpochRecord>());
	}
}
