using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Monitoring;
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.MonitoringTests;

[TestFixture]
public class ConnectionStatsTests
{
	private static readonly DateTimeOffset ConnectedAt = new(2026, 9, 12, 12, 34, 56, TimeSpan.Zero);
	private ConnectionStatsResp _response;

	[SetUp]
	public async Task SetUp()
	{
		var provider = new StubConnectionStatsProvider([
			new ConnectionStatsSnapshot(
				"connection-1",
				"127.0.0.1:50123",
				"127.0.0.1:1112",
				"projection-catchup",
				"gRPC",
				"HTTP/2",
				true,
				ConnectedAt,
				123,
				456,
				7,
				8)
		]);
		var serviceType = typeof(Message).Assembly.GetType(
			"EventStore.Core.Services.Transport.Grpc.Monitoring",
			throwOnError: true);
		var service = Activator.CreateInstance(
			serviceType!,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: [new NoOpPublisher(), provider],
			culture: null);

		var task = (Task<ConnectionStatsResp>)serviceType!.GetMethod(
				nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.ConnectionStats))!
			.Invoke(service, [new ConnectionStatsReq(), TestServerCallContext.Instance])!;
		_response = await task;
	}

	[Test]
	public void should_map_the_active_connection()
	{
		var connection = _response.Connections[0];

		Assert.Multiple(() =>
		{
			Assert.That(connection.ConnectionId, Is.EqualTo("connection-1"));
			Assert.That(connection.RemoteEndpoint, Is.EqualTo("127.0.0.1:50123"));
			Assert.That(connection.LocalEndpoint, Is.EqualTo("127.0.0.1:1112"));
			Assert.That(connection.ClientConnectionName, Is.EqualTo("projection-catchup"));
			Assert.That(connection.Application, Is.EqualTo("gRPC"));
			Assert.That(connection.Protocol, Is.EqualTo("HTTP/2"));
			Assert.That(connection.IsTls, Is.True);
			Assert.That(connection.ConnectedAt.ToDateTimeOffset(), Is.EqualTo(ConnectedAt));
			Assert.That(connection.TotalBytesSent, Is.EqualTo(123));
			Assert.That(connection.TotalBytesReceived, Is.EqualTo(456));
			Assert.That(connection.PendingSendBytes, Is.EqualTo(7));
			Assert.That(connection.PendingReceivedBytes, Is.EqualTo(8));
		});
	}

	private sealed class StubConnectionStatsProvider(IReadOnlyList<ConnectionStatsSnapshot> connections)
		: IConnectionStatsProvider
	{
		public IReadOnlyList<ConnectionStatsSnapshot> Snapshot() => connections;
	}

	private sealed class NoOpPublisher : IPublisher
	{
		public void Publish(Message message)
		{
		}
	}

	private sealed class TestServerCallContext : ServerCallContext
	{
		public static readonly TestServerCallContext Instance = new();

		private TestServerCallContext()
		{
		}

		protected override string MethodCore =>
			nameof(EventStore.Client.Monitoring.Monitoring.MonitoringBase.ConnectionStats);
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
