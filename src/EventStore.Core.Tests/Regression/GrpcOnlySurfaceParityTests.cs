using System;
using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using EventStore.ClusterNode.Components.Services;
using Microsoft.AspNetCore.Connections;
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
	public async Task shared_endpoint_connections_are_visible_only_while_active()
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
}
