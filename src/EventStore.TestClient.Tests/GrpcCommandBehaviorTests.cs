using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.TestClient.GrpcCommands;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace EventStore.TestClient.Tests;

[TestFixture]
public class GrpcCommandBehaviorTests
{
	[Test]
	public void grpc_workloads_keep_the_existing_backpressure_defaults()
	{
		var options = new ClientOptions();

		Assert.Multiple(() =>
		{
			Assert.That(options.PingWindow, Is.EqualTo(2_000));
			Assert.That(options.ReadWindow, Is.EqualTo(2_000));
			Assert.That(options.WriteWindow, Is.EqualTo(2_000));
		});
	}

	[Test]
	public void ping_flood_defaults_preserve_the_historical_total_request_count()
	{
		var workload = FloodWorkload.Parse([], defaultRequestCount: 1_000_000, maxInFlight: 2_000);

		Assert.Multiple(() =>
		{
			Assert.That(workload.ClientCount, Is.EqualTo(1));
			Assert.That(workload.RequestCount, Is.EqualTo(1_000_000));
			Assert.That(workload.RequestsForClient(0), Is.EqualTo(1_000_000));
		});
	}

	[Test]
	public void ping_flood_waiting_defaults_preserve_the_historical_total_request_count()
	{
		var workload = FloodWorkload.Parse([], defaultRequestCount: 100_000, maxInFlight: 1);

		Assert.That(workload.RequestCount, Is.EqualTo(100_000));
	}

	[Test]
	public void flood_requests_are_distributed_as_one_total_across_clients()
	{
		var workload = FloodWorkload.Parse(["4", "10"], defaultRequestCount: 1, maxInFlight: 2_000);

		Assert.Multiple(() =>
		{
			Assert.That(workload.RequestsForClient(0), Is.EqualTo(2));
			Assert.That(workload.RequestsForClient(1), Is.EqualTo(2));
			Assert.That(workload.RequestsForClient(2), Is.EqualTo(2));
			Assert.That(workload.RequestsForClient(3), Is.EqualTo(4));
			Assert.That(workload.MaxInFlightPerClient, Is.EqualTo(500));
		});
	}

	[TestCase(true, "True")]
	[TestCase(false, "False")]
	public async Task read_commands_control_the_requires_leader_header(bool requireLeader, string expectedHeader)
	{
		var handler = new RecordingGrpcHandler();
		var grpcClient = new GrpcTestClient(new ClientOptions(), Serilog.Log.Logger, () =>
		{
			var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
			settings.CreateHttpMessageHandler = () => handler;
			return settings;
		});
		using var client = grpcClient.CreateGrpcClient(requireLeader);
		var read = client.ReadStreamAsync(Direction.Forwards, "test-stream", StreamPosition.Start, maxCount: 1);
		var state = await read.ReadState;

		Assert.Multiple(() =>
		{
			Assert.That(state, Is.EqualTo(ReadState.StreamNotFound));
			Assert.That(handler.Requests.Single(request => request.Path.EndsWith("/Read", StringComparison.Ordinal)).RequiresLeader,
				Is.EqualTo(expectedHeader));
		});
	}

	[TestCase(0, "AccountDebited")]
	[TestCase(1, "AccountCredited")]
	[TestCase(9, "AccountCheckPoint")]
	public void verification_events_preserve_deterministic_typed_json(int eventNumber, string expectedType)
	{
		var first = VerificationEventFactory.Create(eventNumber);
		var second = VerificationEventFactory.Create(eventNumber);

		Assert.Multiple(() =>
		{
			Assert.That(first.Type, Is.EqualTo(expectedType));
			Assert.That(first.ContentType, Is.EqualTo("application/json"));
			Assert.That(first.Data.ToArray(), Is.EqualTo(second.Data.ToArray()));
			Assert.That(() => JObject.Parse(System.Text.Encoding.UTF8.GetString(first.Data.Span)), Throws.Nothing);
		});
	}

	[Test]
	public void delete_command_uses_the_grpc_tombstone_operation()
	{
		var handler = new RecordingGrpcHandler();
		var options = new ClientOptions { Command = ["DEL test-stream ANY"] };
		var grpcClient = new GrpcTestClient(options, Serilog.Log.Logger, () =>
		{
			var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
			settings.CreateHttpMessageHandler = () => handler;
			return settings;
		});
		using var cancellation = new CancellationTokenSource();
		var client = new Client(options, cancellation, grpcClient);

		Assert.Multiple(() =>
		{
			Assert.That(client.Run(cancellation.Token), Is.Zero);
			Assert.That(handler.Requests.Select(request => request.Path),
				Does.Contain("/event_store.client.streams.Streams/Tombstone"));
			Assert.That(handler.Requests.Select(request => request.Path),
				Does.Not.Contain("/event_store.client.streams.Streams/Delete"));
		});
	}

	[Test]
	public void ping_flood_treats_messages_as_one_total_across_clients()
	{
		var handler = new RecordingGrpcHandler();
		var options = new ClientOptions { Command = ["PINGFL 4 10"] };
		var grpcClient = new GrpcTestClient(options, Serilog.Log.Logger, () =>
		{
			var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
			settings.CreateHttpMessageHandler = () => handler;
			return settings;
		});
		using var cancellation = new CancellationTokenSource();
		var client = new Client(options, cancellation, grpcClient);

		Assert.Multiple(() =>
		{
			Assert.That(client.Run(cancellation.Token), Is.Zero);
			Assert.That(handler.Requests.Count(request => request.Path.EndsWith("/Read", StringComparison.Ordinal)),
				Is.EqualTo(10));
		});
	}

	[Test]
	public void grpc_read_all_preserves_backward_direction_and_default_position()
	{
		Assert.That(ReadAllWorkload.TryParse(["B", "1"], out var workload), Is.True);
		Assert.Multiple(() =>
		{
			Assert.That(workload.Direction, Is.EqualTo(Direction.Backwards));
			Assert.That(workload.Position, Is.EqualTo(Position.End));
			Assert.That(workload.ClientCount, Is.EqualTo(1));
		});
	}

	private sealed class RecordingGrpcHandler : HttpMessageHandler
	{
		public ConcurrentBag<RecordedGrpcRequest> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var path = request.RequestUri!.AbsolutePath;
			var requiresLeader = request.Headers.TryGetValues("requires-leader", out var values)
				? values.Single()
				: null;
			Requests.Add(new RecordedGrpcRequest(path, requiresLeader));
			var payload = path.EndsWith("/Tombstone", StringComparison.Ordinal)
				? new byte[] { 0x0a, 0x00 }
				: path.EndsWith("/Read", StringComparison.Ordinal)
					? new byte[] { 0x22, 0x00 }
					: [];
			var frame = new byte[payload.Length + 5];
			frame[4] = (byte)payload.Length;
			payload.CopyTo(frame, 5);
			var response = new HttpResponseMessage(HttpStatusCode.OK)
			{
				Version = HttpVersion.Version20,
				Content = new ByteArrayContent(frame)
			};
			response.Content.Headers.ContentType = new("application/grpc");
			response.TrailingHeaders.Add("grpc-status", "0");
			return Task.FromResult(response);
		}
	}

	private sealed record RecordedGrpcRequest(string Path, string RequiresLeader);
}
