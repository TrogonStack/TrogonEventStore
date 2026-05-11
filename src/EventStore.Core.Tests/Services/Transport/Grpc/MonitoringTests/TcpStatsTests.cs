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
using CoreTcpConnectionStats = EventStore.Core.Messages.MonitoringMessage.TcpConnectionStats;

namespace EventStore.Core.Tests.Services.Transport.Grpc.MonitoringTests;

[TestFixture]
public class TcpStatsTests {
	private readonly Guid _connectionId = Guid.Parse("1e1c6d68-3c7c-446f-915e-8bdf8d35e122");
	private TcpStatsResp _response;
	private CapturingPublisher _publisher;

	[SetUp]
	public async Task SetUp() {
		_publisher = new CapturingPublisher(new List<CoreTcpConnectionStats> {
			new() {
				RemoteEndPoint = "127.0.0.1:1113",
				LocalEndPoint = "127.0.0.1:2113",
				ClientConnectionName = "test-connection",
				ConnectionId = _connectionId,
				TotalBytesSent = 123,
				TotalBytesReceived = 456,
				PendingSendBytes = 7,
				PendingReceivedBytes = 8,
				IsExternalConnection = true,
				IsSslConnection = true
			},
			new() {
				ConnectionId = Guid.Empty
			}
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

		var task = (Task<TcpStatsResp>)serviceType!.GetMethod(nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.TcpStats))!
			.Invoke(service, [new TcpStatsReq(), TestServerCallContext.Instance])!;
		_response = await task;
	}

	[Test]
	public void should_request_fresh_tcp_connection_stats() {
		Assert.IsTrue(_publisher.RequestedTcpStats);
	}

	[Test]
	public void should_return_the_tcp_connection_stats() {
		Assert.AreEqual(2, _response.Connections.Count);
	}

	[Test]
	public void should_map_all_tcp_connection_fields() {
		var connection = _response.Connections[0];

		Assert.AreEqual("127.0.0.1:1113", connection.RemoteEndpoint);
		Assert.AreEqual("127.0.0.1:2113", connection.LocalEndpoint);
		Assert.AreEqual("test-connection", connection.ClientConnectionName);
		Assert.AreEqual(_connectionId.ToString("D"), connection.ConnectionId);
		Assert.AreEqual(123, connection.TotalBytesSent);
		Assert.AreEqual(456, connection.TotalBytesReceived);
		Assert.AreEqual(7, connection.PendingSendBytes);
		Assert.AreEqual(8, connection.PendingReceivedBytes);
		Assert.IsTrue(connection.IsExternalConnection);
		Assert.IsTrue(connection.IsSslConnection);
	}

	[Test]
	public void should_map_null_strings_to_empty_values() {
		var connection = _response.Connections[1];

		Assert.AreEqual(string.Empty, connection.RemoteEndpoint);
		Assert.AreEqual(string.Empty, connection.LocalEndpoint);
		Assert.AreEqual(string.Empty, connection.ClientConnectionName);
	}

	private sealed class CapturingPublisher(List<CoreTcpConnectionStats> connectionStats) : IPublisher {
		public bool RequestedTcpStats { get; private set; }

		public void Publish(Message message) {
			if (message is not MonitoringMessage.GetFreshTcpConnectionStats request) {
				throw new InvalidOperationException($"Unexpected message {message.GetType().Name}");
			}

			RequestedTcpStats = true;
			request.Envelope.ReplyWith(new MonitoringMessage.GetFreshTcpConnectionStatsCompleted(connectionStats));
		}
	}

	private sealed class TestServerCallContext : ServerCallContext {
		public static readonly TestServerCallContext Instance = new();

		private TestServerCallContext() {
		}

		protected override string MethodCore => nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.TcpStats);
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
