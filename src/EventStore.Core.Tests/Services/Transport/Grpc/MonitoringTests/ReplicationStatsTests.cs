using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using Grpc.Core;
using NUnit.Framework;
using CoreReplicationStats = EventStore.Core.Messages.ReplicationMessage.ReplicationStats;

namespace EventStore.Core.Tests.Services.Transport.Grpc.MonitoringTests;

[TestFixture]
public class ReplicationStatsTests {
	private readonly Guid _subscriptionId = Guid.Parse("3c870871-1a1a-48f1-a9d6-89f471512f1e");
	private readonly Guid _connectionId = Guid.Parse("d8b2ac45-2510-4a29-9e2a-713a5af7d6c5");
	private ReplicationStatsResp _response;
	private CapturingPublisher _publisher;

	[SetUp]
	public async Task SetUp() {
		_publisher = new CapturingPublisher(new List<CoreReplicationStats> {
			new(
				_subscriptionId,
				_connectionId,
				"127.0.0.1:1112",
				sendQueueSize: 9,
				totalBytesSent: 123,
				totalBytesReceived: 456,
				pendingSendBytes: 7,
				pendingReceivedBytes: 8),
			new(
				Guid.Empty,
				Guid.Empty,
				null,
				sendQueueSize: 0,
				totalBytesSent: 0,
				totalBytesReceived: 0,
				pendingSendBytes: 0,
				pendingReceivedBytes: 0)
		});
		var serviceType = typeof(MonitoringMessage).Assembly.GetType(
			"EventStore.Core.Services.Transport.Grpc.Monitoring",
			throwOnError: true);
		var service = Activator.CreateInstance(
			serviceType!,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: [_publisher],
			culture: null);

		var task = (Task<ReplicationStatsResp>)serviceType!.GetMethod(
				nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.ReplicationStats))!
			.Invoke(service, [new ReplicationStatsReq(), TestServerCallContext.Instance])!;
		_response = await task;
	}

	[Test]
	public void should_request_replication_stats() {
		Assert.IsTrue(_publisher.RequestedReplicationStats);
	}

	[Test]
	public void should_return_the_replication_stats() {
		Assert.AreEqual(2, _response.Stats.Count);
	}

	[Test]
	public void should_map_all_replication_stats_fields() {
		var stats = _response.Stats[0];

		Assert.AreEqual(_subscriptionId.ToString("D"), stats.SubscriptionId);
		Assert.AreEqual(_connectionId.ToString("D"), stats.ConnectionId);
		Assert.AreEqual("127.0.0.1:1112", stats.SubscriptionEndpoint);
		Assert.AreEqual(123, stats.TotalBytesSent);
		Assert.AreEqual(456, stats.TotalBytesReceived);
		Assert.AreEqual(7, stats.PendingSendBytes);
		Assert.AreEqual(8, stats.PendingReceivedBytes);
		Assert.AreEqual(9, stats.SendQueueSize);
	}

	[Test]
	public void should_map_null_strings_to_empty_values() {
		Assert.AreEqual(string.Empty, _response.Stats[1].SubscriptionEndpoint);
	}

	private sealed class CapturingPublisher(List<CoreReplicationStats> replicationStats) : IPublisher {
		public bool RequestedReplicationStats { get; private set; }

		public void Publish(Message message) {
			if (message is not ReplicationMessage.GetReplicationStats request)
				throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");

			RequestedReplicationStats = true;
			request.Envelope.ReplyWith(new ReplicationMessage.GetReplicationStatsCompleted(replicationStats));
		}
	}

	private sealed class TestServerCallContext : ServerCallContext {
		public static readonly TestServerCallContext Instance = new();

		private TestServerCallContext() {
		}

		protected override string MethodCore =>
			nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.ReplicationStats);
		protected override string HostCore => "localhost";
		protected override string PeerCore => "ipv4:127.0.0.1:0";
		protected override DateTime DeadlineCore => DateTime.MaxValue;
		protected override Metadata RequestHeadersCore { get; } = new();
		protected override CancellationToken CancellationTokenCore => CancellationToken.None;
		protected override Metadata ResponseTrailersCore { get; } = new();
		protected override Status StatusCore { get; set; }
		protected override WriteOptions WriteOptionsCore { get; set; }
		protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());
		protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();
		protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) =>
			throw new NotSupportedException();
		protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
	}
}
