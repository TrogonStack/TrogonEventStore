using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using EventStore.Core;
using EventStore.Core.Authorization;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Plugins.Authorization;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace EventStore.Core.Tests.Regression;

[TestFixture]
public class GrpcOnlySurfaceParityTests
{
	[Test]
	public void observability_payload_preserves_replication_visibility()
	{
		var page = QueueDashboardPage.Success(Array.Empty<QueueDashboardRow>());
		using var payload = JsonDocument.Parse(page.ClientPayloadJson);

		Assert.That(payload.RootElement.TryGetProperty("replicationConnections", out _), Is.True);
		Assert.That(payload.RootElement.TryGetProperty("nodeConnections", out _), Is.True);
	}

	[Test]
	public async Task replication_stats_failure_does_not_hide_queue_statistics()
	{
		var publisher = new QueueStatsPublisher();
		var components = new StandardComponents(
			null, null, null, null, null, null, null, publisher, null, null, false);
		var service = new QueueDashboardService(
			new PassthroughAuthorizationProvider(),
			new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
			components,
			new NodeConnectionTracker());

		var page = await service.Read();

		Assert.Multiple(() =>
		{
			Assert.That(page.IsAvailable, Is.True);
			Assert.That(page.Queues, Has.One.Matches<QueueDashboardRow>(x => x.Name == "mainQueue"));
			Assert.That(page.ReplicationMessage, Does.StartWith("Unable to read replication statistics:"));
		});
	}

	[Test]
	public async Task queue_stats_failure_does_not_hide_active_connections()
	{
		var (tracker, release, tracking) = TrackActiveConnection();

		try
		{
			var components = new StandardComponents(
				null, null, null, null, null, null, null, new QueueStatsPublisher(failQueueStats: true),
				null, null, false);
			var service = new QueueDashboardService(
				new PassthroughAuthorizationProvider(),
				new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
				components,
				tracker);

			var page = await service.Read();
			using var payload = JsonDocument.Parse(page.ClientPayloadJson);

			Assert.Multiple(() =>
			{
				Assert.That(page.IsAvailable, Is.False);
				Assert.That(page.NodeConnections, Has.One.Matches<NodeConnectionSnapshot>(x =>
					x.ConnectionId == "active-connection"));
				Assert.That(payload.RootElement.GetProperty("nodeConnections").GetArrayLength(), Is.EqualTo(1));
				Assert.That(payload.RootElement.TryGetProperty("networkAvailable", out var networkAvailable), Is.True);
				Assert.That(networkAvailable.GetBoolean(), Is.True);
			});
		}
		finally
		{
			release.SetResult();
			await tracking;
		}
	}

	[Test]
	public async Task queue_stats_failure_does_not_hide_replication_connections()
	{
		var components = new StandardComponents(
			null, null, null, null, null, null, null,
			new QueueStatsPublisher(failQueueStats: true, provideReplicationStats: true),
			null, null, false);
		var service = new QueueDashboardService(
			new PassthroughAuthorizationProvider(),
			new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
			components,
			new NodeConnectionTracker());

		var page = await service.Read();
		using var payload = JsonDocument.Parse(page.ClientPayloadJson);

		Assert.Multiple(() =>
		{
			Assert.That(page.IsAvailable, Is.False);
			Assert.That(page.ReplicationConnections,
				Has.One.Matches<ReplicationConnectionRow>(x => x.Endpoint == "replica:1112"));
			Assert.That(payload.RootElement.GetProperty("replicationConnections").GetArrayLength(), Is.EqualTo(1));
		});
	}

	[Test]
	public async Task replication_statistics_require_replication_access_without_hiding_other_diagnostics()
	{
		var (tracker, release, tracking) = TrackActiveConnection();
		try
		{
			var publisher = new QueueStatsPublisher(provideReplicationStats: true);
			var authorization = new ReadOnlyStatisticsAuthorizationProvider();
			var components = new StandardComponents(
				null, null, null, null, null, null, null, publisher, null, null, false);
			var service = new QueueDashboardService(
				authorization,
				new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
				components,
				tracker);

			var page = await service.Read();
			using var payload = JsonDocument.Parse(page.ClientPayloadJson);

			Assert.Multiple(() =>
			{
				Assert.That(page.IsAvailable, Is.True);
				Assert.That(page.Queues, Has.One.Matches<QueueDashboardRow>(x => x.Name == "mainQueue"));
				Assert.That(page.NodeConnections, Has.One.Matches<NodeConnectionSnapshot>(x =>
					x.ConnectionId == "active-connection"));
				Assert.That(page.ReplicationConnections, Is.Empty);
				Assert.That(page.ReplicationMessage, Is.EqualTo("Replication statistics access was denied."));
				Assert.That(payload.RootElement.GetProperty("replicationConnections").GetArrayLength(), Is.Zero);
				Assert.That(publisher.ReplicationRequests, Is.Zero);
				Assert.That(authorization.RequestedOperations, Is.EquivalentTo(new[]
				{
					new Operation(Operations.Node.Statistics.Read),
					new Operation(Operations.Node.Statistics.Replication)
				}));
			});
		}
		finally
		{
			release.SetResult();
			await tracking;
		}
	}

	[Test]
	public async Task denied_statistics_access_does_not_expose_or_mark_connections_available()
	{
		var (tracker, release, tracking) = TrackActiveConnection();
		try
		{
			var components = new StandardComponents(
				null, null, null, null, null, null, null, new QueueStatsPublisher(),
				null, null, false);
			var service = new QueueDashboardService(
				new DenyingAuthorizationProvider(),
				new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
				components,
				tracker);

			var page = await service.Read();
			using var payload = JsonDocument.Parse(page.ClientPayloadJson);

			Assert.Multiple(() =>
			{
				Assert.That(page.IsAvailable, Is.False);
				Assert.That(page.NodeConnections, Is.Empty);
				Assert.That(payload.RootElement.GetProperty("nodeConnections").GetArrayLength(), Is.Zero);
				Assert.That(payload.RootElement.TryGetProperty("networkAvailable", out var networkAvailable), Is.True);
				Assert.That(networkAvailable.GetBoolean(), Is.False);
			});
		}
		finally
		{
			release.SetResult();
			await tracking;
		}
	}

	[Test]
	public async Task http_connections_are_visible_only_while_active()
	{
		var tracker = new NodeConnectionTracker();
		var connection = new DefaultConnectionContext("grpc-connection")
		{
			LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 2113),
			RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 50123)
		};
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var trafficObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var incoming = new Pipe();
		var outgoing = new Pipe();
		connection.Transport = new TestDuplexPipe(incoming.Reader, outgoing.Writer);
		var tracking = tracker.Track(connection, async trackedConnection =>
		{
			tracker.ObserveRequest(
				trackedConnection.ConnectionId,
				"HTTP/2",
				isGrpc: true,
				connectionName: "projection-catchup",
				userAgent: "grpc-dotnet");
			await trackedConnection.Transport.Output.WriteAsync(new byte[3]);
			await incoming.Writer.WriteAsync(new byte[5]);
			var read = await trackedConnection.Transport.Input.ReadAsync();
			trackedConnection.Transport.Input.AdvanceTo(read.Buffer.GetPosition(2), read.Buffer.End);
			trafficObserved.SetResult();
			await release.Task;
		}, isTls: true);
		await trafficObserved.Task;

		Assert.That(tracker.Snapshot(), Has.One.Matches<NodeConnectionSnapshot>(x =>
			x.ConnectionId == "grpc-connection" && x.IsTls &&
			x.ClientName == "projection-catchup" && x.Application == "gRPC" && x.Protocol == "HTTP/2" &&
			x.TotalBytesSent == 3 && x.TotalBytesReceived == 2 && x.PendingReceivedBytes == 3));

		tracker.ObserveRequest(
			connection.ConnectionId,
			"HTTP/2",
			isGrpc: false,
			connectionName: "",
			userAgent: "browser");
		Assert.That(tracker.Snapshot(), Has.One.Matches<NodeConnectionSnapshot>(x =>
			x.ClientName == "projection-catchup" && x.Application == "HTTP and gRPC"));

		release.SetResult();
		await tracking;

		Assert.That(tracker.Snapshot(), Is.Empty);
	}

	private sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
	{
		public PipeReader Input { get; } = input;
		public PipeWriter Output { get; } = output;
	}

	private static (NodeConnectionTracker Tracker, TaskCompletionSource Release, Task Tracking)
		TrackActiveConnection()
	{
		var tracker = new NodeConnectionTracker();
		var connection = new DefaultConnectionContext("active-connection")
		{
			LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 2113),
			RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 50123),
			Transport = new TestDuplexPipe(new Pipe().Reader, new Pipe().Writer)
		};
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var tracking = tracker.Track(connection, _ => release.Task, isTls: false);
		return (tracker, release, tracking);
	}

	private sealed class DenyingAuthorizationProvider : PassthroughAuthorizationProvider
	{
		public override ValueTask<bool> CheckAccessAsync(
			ClaimsPrincipal principal, Operation operation, CancellationToken cancellationToken) =>
			ValueTask.FromResult(false);
	}

	private sealed class ReadOnlyStatisticsAuthorizationProvider : PassthroughAuthorizationProvider
	{
		public List<Operation> RequestedOperations { get; } = new();

		public override ValueTask<bool> CheckAccessAsync(
			ClaimsPrincipal principal, Operation operation, CancellationToken cancellationToken)
		{
			RequestedOperations.Add(operation);
			return ValueTask.FromResult(!operation.Equals(new Operation(Operations.Node.Statistics.Replication)));
		}
	}

	private sealed class QueueStatsPublisher(bool failQueueStats = false, bool provideReplicationStats = false) : IPublisher
	{
		public int ReplicationRequests { get; private set; }

		public void Publish(Message message)
		{
			switch (message)
			{
				case MonitoringMessage.GetFreshStats request:
					if (failQueueStats)
					{
						throw new InvalidOperationException("Queue statistics are unavailable.");
					}

					request.Envelope.ReplyWith(new MonitoringMessage.GetFreshStatsCompleted(
						success: true,
						stats: new Dictionary<string, object>
						{
							["es"] = new Dictionary<string, object>
							{
								["queue"] = new Dictionary<string, object>
								{
									["mainQueue"] = new Dictionary<string, object>
									{
										["queueName"] = "mainQueue"
									}
								}
							}
						}));
					break;
				case ReplicationMessage.GetReplicationStats request:
					ReplicationRequests++;
					if (!provideReplicationStats)
					{
						throw new InvalidOperationException("Replication statistics are unavailable.");
					}

					request.Envelope.ReplyWith(new ReplicationMessage.GetReplicationStatsCompleted(
						[new ReplicationMessage.ReplicationStats(
							Guid.NewGuid(), Guid.NewGuid(), "replica:1112", 0, 0, 0, 0, 0)]));
					break;
			}
		}
	}
}
