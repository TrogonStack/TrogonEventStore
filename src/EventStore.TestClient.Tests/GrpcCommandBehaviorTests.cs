using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
	public void ping_uses_a_user_stream_for_unauthenticated_connectivity_checks()
	{
		var handler = new RecordingGrpcHandler();
		var result = RunCommand("PING", handler);
		var request = handler.Requests.Single(request => request.Path.EndsWith("/Read", StringComparison.Ordinal));

		Assert.Multiple(() =>
		{
			Assert.That(result, Is.Zero);
			Assert.That(Encoding.UTF8.GetString(request.Body), Does.Not.Contain("$test-client-ping"));
		});
	}

	[TestCase("SUBSCR", true, null)]
	[TestCase("SUBSCR test-stream", false, "test-stream")]
	[TestCase("SST 1", false, "stream-0")]
	public async Task subscriptions_start_from_the_live_position(string command, bool subscribeToAll, string stream)
	{
		var expected = await RecordSubscriptionRequest(subscribeToAll, stream);
		var handler = new RecordingGrpcHandler
		{
			Failure = new HttpRequestException("stop after recording"),
			FailurePath = "/event_store.client.streams.Streams/Read"
		};

		RunCommand(command, handler);

		Assert.That(
			handler.Requests.Single(request => request.Path == "/event_store.client.streams.Streams/Read").Body,
			Is.EqualTo(expected));
	}

	[Test]
	public void read_flood_completes_every_request_before_reporting_missing_streams()
	{
		var handler = new RecordingGrpcHandler();
		var result = RunCommand("RDFL 2 5 1 missing-stream", handler, new ClientOptions { ReadWindow = 1 });

		Assert.Multiple(() =>
		{
			Assert.That(result, Is.Not.Zero);
			Assert.That(handler.Requests.Count(request => request.Path.EndsWith("/Read", StringComparison.Ordinal)),
				Is.EqualTo(5));
		});
	}

	[Test]
	public void read_command_fails_when_the_requested_event_is_missing()
	{
		var handler = new RecordingGrpcHandler { ReadPayload = [] };

		Assert.That(RunCommand("RD test-stream 42", handler), Is.Not.Zero);
	}

	[Test]
	public async Task read_command_preserves_the_last_event_sentinel()
	{
		var expected = await RecordStreamReadRequest(Direction.Backwards, StreamPosition.End);
		var handler = new RecordingGrpcHandler
		{
			Failure = new HttpRequestException("stop after recording"),
			FailurePath = "/event_store.client.streams.Streams/Read"
		};

		RunCommand("RD test-stream -1", handler);

		Assert.That(
			handler.Requests.Single(request => request.Path == "/event_store.client.streams.Streams/Read").Body,
			Is.EqualTo(expected));
	}

	[Test]
	public async Task read_all_accepts_explicit_end_position_sentinels()
	{
		var expected = await RecordReadAllRequest(Direction.Backwards, Position.End);
		var handler = new RecordingGrpcHandler { ReadPayload = [] };

		var result = RunCommand("RDALL B -1 -1", handler);

		Assert.Multiple(() =>
		{
			Assert.That(result, Is.Zero);
			Assert.That(
				handler.Requests.Single(request => request.Path == "/event_store.client.streams.Streams/Read").Body,
				Is.EqualTo(expected));
		});
	}

	[TestCase("-1", "NOSTREAM")]
	[TestCase("-2", "ANY")]
	public void expected_version_sentinels_preserve_their_historical_meaning(string sentinel, string name)
	{
		var expectedHandler = new RecordingGrpcHandler();
		Assert.That(RunCommand($"DEL test-stream {name}", expectedHandler), Is.Zero);

		var sentinelHandler = new RecordingGrpcHandler();
		Assert.That(RunCommand($"DEL test-stream {sentinel}", sentinelHandler), Is.Zero);

		Assert.That(
			sentinelHandler.Requests.Single(request => request.Path.EndsWith("/Tombstone", StringComparison.Ordinal)).Body,
			Is.EqualTo(expectedHandler.Requests.Single(request => request.Path.EndsWith("/Tombstone", StringComparison.Ordinal)).Body));
	}

	[Test]
	public void grpc_http_endpoint_uses_the_resolved_single_node_address()
	{
		var settings = EventStoreClientSettings.Create("esdb://configured.example:3210?tls=false");
		var grpcClient = new GrpcTestClient(
			new ClientOptions { Host = "ignored.example", HttpPort = 9999 },
			Serilog.Log.Logger,
			() => settings);

		Assert.That(grpcClient.HttpEndpoint, Is.EqualTo(settings.ConnectivitySettings.Address));
	}

	[Test]
	public void grpc_http_endpoint_rejects_discovery_connections()
	{
		var settings = EventStoreClientSettings.Create("esdb+discover://localhost:2113?tls=false");
		var grpcClient = new GrpcTestClient(new ClientOptions(), Serilog.Log.Logger, () => settings);

		Assert.That(() => grpcClient.HttpEndpoint, Throws.InvalidOperationException);
	}

	[Test]
	public void what_if_options_do_not_render_connection_string_credentials()
	{
		var rendered = new ClientOptions
		{
			ConnectionString = "esdb://test-user:test-password@localhost:2113?tls=false"
		}.ToString();

		Assert.Multiple(() =>
		{
			Assert.That(rendered, Does.Not.Contain("test-user"));
			Assert.That(rendered, Does.Not.Contain("test-password"));
			Assert.That(rendered, Does.Contain("ConnectionString: [REDACTED]"));
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

	private static int RunCommand(string command, RecordingGrpcHandler handler, ClientOptions options = null)
	{
		options = (options ?? new ClientOptions()) with { Command = [command] };
		var grpcClient = new GrpcTestClient(options, Serilog.Log.Logger, () =>
		{
			var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
			settings.CreateHttpMessageHandler = () => handler;
			return settings;
		});
		using var cancellation = new CancellationTokenSource();
		return new Client(options, cancellation, grpcClient).Run(cancellation.Token);
	}

	private static async Task<byte[]> RecordSubscriptionRequest(bool subscribeToAll, string stream)
	{
		var handler = new RecordingGrpcHandler
		{
			Failure = new HttpRequestException("stop after recording"),
			FailurePath = "/event_store.client.streams.Streams/Read"
		};
		var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
		settings.CreateHttpMessageHandler = () => handler;
		using var client = new EventStoreClient(settings);
		try
		{
			if (subscribeToAll)
			{
				await client.SubscribeToAllAsync(FromAll.End, (_, _, _) => Task.CompletedTask);
			}
			else
			{
				await client.SubscribeToStreamAsync(stream, FromStream.End, (_, _, _) => Task.CompletedTask);
			}
		}
		catch (Grpc.Core.RpcException)
		{
		}

		return handler.Requests.Single(request => request.Path == "/event_store.client.streams.Streams/Read").Body;
	}

	private static async Task<byte[]> RecordStreamReadRequest(Direction direction, StreamPosition position)
	{
		var handler = new RecordingGrpcHandler
		{
			Failure = new HttpRequestException("stop after recording"),
			FailurePath = "/event_store.client.streams.Streams/Read"
		};
		using var client = CreateClient(handler);
		try
		{
			var read = client.ReadStreamAsync(direction, "test-stream", position, maxCount: 1);
			await read.ReadState;
		}
		catch (Grpc.Core.RpcException)
		{
		}

		return handler.Requests.Single(request => request.Path == "/event_store.client.streams.Streams/Read").Body;
	}

	private static async Task<byte[]> RecordReadAllRequest(Direction direction, Position position)
	{
		var handler = new RecordingGrpcHandler
		{
			Failure = new HttpRequestException("stop after recording"),
			FailurePath = "/event_store.client.streams.Streams/Read"
		};
		using var client = CreateClient(handler);
		try
		{
			var read = client.ReadAllAsync(direction, position);
			await foreach (var _ in read.Messages)
			{
			}
		}
		catch (Grpc.Core.RpcException)
		{
		}

		return handler.Requests.Single(request => request.Path == "/event_store.client.streams.Streams/Read").Body;
	}

	private static EventStoreClient CreateClient(HttpMessageHandler handler)
	{
		var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
		settings.CreateHttpMessageHandler = () => handler;
		return new EventStoreClient(settings);
	}

	private sealed class RecordingGrpcHandler : HttpMessageHandler
	{
		public ConcurrentBag<RecordedGrpcRequest> Requests { get; } = [];
		public Exception Failure { get; init; }
		public string FailurePath { get; init; }
		public byte[] ReadPayload { get; init; } = [0x22, 0x00];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var path = request.RequestUri!.AbsolutePath;
			var requiresLeader = request.Headers.TryGetValues("requires-leader", out var values)
				? values.Single()
				: null;
			var body = request.Content is null
				? []
				: await request.Content.ReadAsByteArrayAsync(cancellationToken);
			Requests.Add(new RecordedGrpcRequest(path, requiresLeader, body));
			if (Failure is not null && (FailurePath is null || path == FailurePath))
			{
				throw Failure;
			}

			var payload = path.EndsWith("/Tombstone", StringComparison.Ordinal)
				? new byte[] { 0x0a, 0x00 }
				: path.EndsWith("/Read", StringComparison.Ordinal)
					? ReadPayload
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
			return response;
		}
	}

	private sealed record RecordedGrpcRequest(string Path, string RequiresLeader, byte[] Body);
}
