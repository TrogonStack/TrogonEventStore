using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.TestClient.Commands;

namespace EventStore.TestClient.GrpcCommands;

internal sealed class CompatibilityProcessor : ICmdProcessor
{
	private static readonly UTF8Encoding Utf8NoBom = new(false);
	private readonly Func<CommandProcessorContext, string[], Task> _execute;
	private readonly Func<string[], bool> _validate;

	public CompatibilityProcessor(
		string keyword,
		string usage,
		Func<CommandProcessorContext, string[], Task> execute,
		Func<string[], bool> validate = null)
	{
		Keyword = keyword;
		Usage = usage;
		_execute = execute;
		_validate = validate ?? (_ => true);
	}

	public string Keyword { get; }
	public string Usage { get; }

	public bool Execute(CommandProcessorContext context, string[] args)
	{
		if (!_validate(args))
		{
			return false;
		}

		context.IsAsync();
		_ = Complete(context, args);
		return true;
	}

	private async Task Complete(CommandProcessorContext context, string[] args)
	{
		try
		{
			await _execute(context, args);
			context.Success();
		}
		catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
		{
			context.Success();
		}
		catch (Exception ex)
		{
			context.Fail(ex);
		}
	}

	public static IEnumerable<ICmdProcessor> CreateSupportedProcessors()
	{
		yield return new CompatibilityProcessor("PING", "PING", Ping, args => args.Length == 0);
		yield return new CompatibilityProcessor("PINGFL", "PINGFL [<clients> <messages>]", PingFlood,
			args => args.Length == 0 || args.Length == 2);
		yield return new CompatibilityProcessor("PINGFLW", "PINGFLW [<clients> <messages>]", PingFloodWaiting,
			args => args.Length == 0 || args.Length == 2);
		yield return new CompatibilityProcessor("WR", "WR [<stream-id> <expected-version> <data> [<metadata> [<is-json> [<login> <pass>]]]", Write,
			args => args.Length == 0 || args.Length is >= 3 and <= 7 && args.Length != 6);
		yield return new CompatibilityProcessor("WRJ", "WRJ [<stream-id> <expected-version> <data> [<metadata>]]", WriteJson,
			args => args.Length == 0 || args.Length is >= 3 and <= 4);
		yield return new CompatibilityProcessor("MWR", "MWR [<write-count=10> [<stream=test-stream> [<expected-version=ANY>]]]", MultiWrite,
			args => args.Length <= 3);
		yield return new CompatibilityProcessor("WRFLW", "WRFLW [<clients> <requests> [payload-size]]", WriteFloodWaiting,
			args => args.Length == 0 || args.Length is 2 or 3);
		yield return new CompatibilityProcessor("MWRFLW", "MWRFLW [<events-count> [<clients> <requests>]]", MultiWriteFloodWaiting,
			args => args.Length is 0 or 1 or 3);
		yield return new CompatibilityProcessor("DEL", "DEL [<stream-id> [<expected-version>]]", Delete,
			args => args.Length <= 2);
		yield return new CompatibilityProcessor("RD", "RD [<stream-id> [<from-number> [<only-if-leader>]]]", Read,
			args => args.Length <= 3);
		yield return new CompatibilityProcessor("RDFL", "RDFL [<clients> <requests> [<streams-cnt> [<stream-prefix> [<require-leader>]]]]", ReadFlood,
			args => args.Length == 0 || args.Length is >= 2 and <= 5);
		yield return new CompatibilityProcessor("RDALL", "RDALL [[F|B] [<commit pos> <prepare pos> [<only-if-leader>]]]", ReadAll,
			args => args.Length is 0 or 1 or 3 or 4);
		yield return new CompatibilityProcessor("WRLT", "WRLT <clients> <min req. per second> <max req. per second> <run for n minutes> [<event-stream>]", WriteLongTerm,
			args => args.Length == 0 || args.Length is 4 or 5);
		yield return new CompatibilityProcessor("SUBSCR", "SUBSCR [<stream_1> <stream_2> ... <stream_n>]", Subscribe,
			args => true);
		yield return new CompatibilityProcessor("SST", "SST [<subscription-count>]", SubscriptionStress,
			args => args.Length <= 1);
		yield return new CompatibilityProcessor("SCAVENGE", "SCAVENGE", Scavenge,
			args => args.Length == 0);
		yield return new CompatibilityProcessor("VERIFY", "VERIFY <writers> <readers> <events> <streams> <producers>", Verify,
			args => args.Length == 0 || args.Length == 5);
		yield return new CompatibilityProcessor("CHKGRPC", "CHKGRPC", CheckGrpcSanitization,
			args => args.Length == 0);
	}

	private static EventData Event(
		string data = "test-data",
		string metadata = "",
		bool isJson = false,
		string eventType = "TakeSomeSpaceEvent") =>
		new(Uuid.FromGuid(Guid.NewGuid()), eventType, Utf8NoBom.GetBytes(data), Utf8NoBom.GetBytes(metadata),
			isJson ? "application/json" : "application/octet-stream");

	private static async Task Append(
		EventStoreClient client,
		string stream,
		ExpectedRevision expected,
		IEnumerable<EventData> events,
		UserCredentials credentials,
		CancellationToken cancellationToken)
	{
		if (expected.Revision is { } revision)
		{
			await client.AppendToStreamAsync(stream, revision, events, userCredentials: credentials, cancellationToken: cancellationToken);
		}
		else
		{
			await client.AppendToStreamAsync(stream, expected.State, events, userCredentials: credentials, cancellationToken: cancellationToken);
		}
	}

	private static async Task Ping(CommandProcessorContext context, string[] args)
	{
		var client = context._grpcTestClient.CreateGrpcClient(requireLeader: false);
		await Ping(client, context.CancellationToken);
	}

	private static async Task Ping(EventStoreClient client, CancellationToken cancellationToken)
	{
		var read = client.ReadStreamAsync(Direction.Forwards, $"test-client-ping-{Guid.NewGuid():N}", StreamPosition.Start,
			maxCount: 1, cancellationToken: cancellationToken);
		await read.ReadState;
	}

	private static async Task PingFlood(CommandProcessorContext context, string[] args)
	{
		var workload = FloodWorkload.Parse(
			args,
			defaultRequestCount: 1_000_000,
			maxInFlight: context._grpcTestClient.Options.PingWindow);
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		await Task.WhenAll(Enumerable.Range(0, workload.ClientCount).Select(clientIndex =>
		{
			var client = context._grpcTestClient.CreateGrpcClient(requireLeader: false);
			return RunBounded(
				workload.RequestsForClient(clientIndex),
				workload.MaxInFlightPerClient,
				() => Ping(client, context.CancellationToken));
		}));
		stopwatch.Stop();
		LogFloodCompletion(context, "PINGFL", workload, stopwatch.Elapsed);
	}

	private static async Task PingFloodWaiting(CommandProcessorContext context, string[] args)
	{
		var workload = FloodWorkload.Parse(args, defaultRequestCount: 100_000, maxInFlight: 1);
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		await Task.WhenAll(Enumerable.Range(0, workload.ClientCount).Select(async clientIndex =>
		{
			var client = context._grpcTestClient.CreateGrpcClient(requireLeader: false);
			for (long i = 0; i < workload.RequestsForClient(clientIndex); i++)
			{
				await Ping(client, context.CancellationToken);
			}
		}));
		stopwatch.Stop();
		LogFloodCompletion(context, "PINGFLW", workload, stopwatch.Elapsed, includeLatency: true);
	}

	private static void LogFloodCompletion(
		CommandProcessorContext context,
		string command,
		FloodWorkload workload,
		TimeSpan elapsed,
		bool includeLatency = false)
	{
		var rate = elapsed.TotalMilliseconds == 0
			? 0
			: workload.RequestCount / elapsed.TotalMilliseconds * 1_000;
		context.Log.Information(
			"{requests} requests completed in {elapsed}ms ({rate:0.00} reqs per sec).",
			workload.RequestCount,
			elapsed.TotalMilliseconds,
			rate);
		PerfUtils.LogData(
			command,
			PerfUtils.Row(
				PerfUtils.Col("clientsCnt", workload.ClientCount),
				PerfUtils.Col("requestsCnt", workload.RequestCount),
				PerfUtils.Col("ElapsedMilliseconds", elapsed.TotalMilliseconds)));
		PerfUtils.LogTeamCityGraphData(
			$"{command}-{workload.ClientCount}-{workload.RequestCount}-reqPerSec",
			(int)rate);
		if (includeLatency && workload.RequestCount > 0)
		{
			PerfUtils.LogTeamCityGraphData(
				$"{command}-latency-ms",
				(int)Math.Round(elapsed.TotalMilliseconds / workload.RequestCount));
		}
	}

	private static async Task RunBounded(long requestCount, int maxInFlight, Func<Task> operation)
	{
		var pending = new List<Task>(maxInFlight);
		for (long request = 0; request < requestCount; request++)
		{
			pending.Add(operation());
			if (pending.Count < maxInFlight)
			{
				continue;
			}

			await Task.WhenAny(pending);
			for (var index = pending.Count - 1; index >= 0; index--)
			{
				if (pending[index].IsCompleted)
				{
					await pending[index];
					pending.RemoveAt(index);
				}
			}
		}

		await Task.WhenAll(pending);
	}

	private static async Task Write(CommandProcessorContext context, string[] args)
	{
		var stream = args.Length == 0 ? "test-stream" : args[0];
		var expected = ExpectedRevision.Parse(args.Length == 0 ? "ANY" : args[1]);
		var data = args.Length == 0 ? "test-data" : args[2];
		var metadata = args.Length >= 4 ? args[3] : "";
		var isJson = args.Length >= 5 && bool.Parse(args[4]);
		var credentials = args.Length == 7 ? new UserCredentials(args[5], args[6]) : null;
		await Append(context._grpcTestClient.CreateGrpcClient(), stream, expected, [Event(data, metadata, isJson)],
			credentials, context.CancellationToken);
	}

	private static Task WriteJson(CommandProcessorContext context, string[] args)
	{
		if (args.Length == 0)
		{
			return Write(context, ["test-stream", "ANY", "{\"value\":\"test\"}", "", "true"]);
		}

		var forwarded = args.Concat(["true"]).ToArray();
		if (args.Length == 3)
		{
			forwarded = [args[0], args[1], args[2], "", "true"];
		}

		return Write(context, forwarded);
	}

	private static async Task MultiWrite(CommandProcessorContext context, string[] args)
	{
		var count = args.Length == 0 ? 10 : MetricPrefixValue.ParseInt(args[0]);
		var stream = args.Length >= 2 ? args[1] : "test-stream";
		var expected = ExpectedRevision.Parse(args.Length >= 3 ? args[2] : "ANY");
		await Append(context._grpcTestClient.CreateGrpcClient(), stream, expected,
			Enumerable.Range(0, count).Select(_ => Event(eventType: "type")), null, context.CancellationToken);
	}

	private static async Task WriteFloodWaiting(CommandProcessorContext context, string[] args)
	{
		var clients = args.Length == 0 ? 1 : MetricPrefixValue.ParseInt(args[0]);
		var requests = args.Length == 0 ? 5000 : MetricPrefixValue.ParseInt(args[1]);
		var payloadSize = args.Length == 3 ? MetricPrefixValue.ParseInt(args[2]) : 356;
		var dataSize = Math.Max(0, payloadSize - 100);
		var metadataSize = Math.Min(100, payloadSize);
		var data = "DATA" + new string('*', dataSize);
		var metadata = "METADATA" + new string('$', metadataSize);
		await Task.WhenAll(Enumerable.Range(0, clients).Select(async clientIndex =>
		{
			var client = context._grpcTestClient.CreateGrpcClient();
			var stream = $"write-flood-waiting-{clientIndex}-{Guid.NewGuid():N}";
			for (var i = clientIndex; i < requests; i += clients)
			{
				await client.AppendToStreamAsync(stream, StreamState.Any, [Event(data, metadata)],
					cancellationToken: context.CancellationToken);
			}
		}));
	}

	private static async Task MultiWriteFloodWaiting(CommandProcessorContext context, string[] args)
	{
		var eventCount = args.Length == 0 ? 10 : MetricPrefixValue.ParseInt(args[0]);
		var clients = args.Length == 3 ? MetricPrefixValue.ParseInt(args[1]) : 1;
		var requests = args.Length == 3 ? MetricPrefixValue.ParseInt(args[2]) : 5000;
		await Task.WhenAll(Enumerable.Range(0, clients).Select(async clientIndex =>
		{
			var client = context._grpcTestClient.CreateGrpcClient();
			var stream = $"multi-write-flood-waiting-{clientIndex}-{Guid.NewGuid():N}";
			for (var i = clientIndex; i < requests; i += clients)
			{
				await client.AppendToStreamAsync(stream, StreamState.Any,
					Enumerable.Range(0, eventCount).Select(_ => Event(eventType: "type")),
					cancellationToken: context.CancellationToken);
			}
		}));
	}

	private static async Task Delete(CommandProcessorContext context, string[] args)
	{
		var stream = args.Length >= 1 ? args[0] : "test-stream";
		var expected = ExpectedRevision.Parse(args.Length >= 2 ? args[1] : "ANY");
		var client = context._grpcTestClient.CreateGrpcClient();
		if (expected.Revision is { } revision)
		{
			await client.TombstoneAsync(stream, revision, cancellationToken: context.CancellationToken);
		}
		else
		{
			await client.TombstoneAsync(stream, expected.State, cancellationToken: context.CancellationToken);
		}
	}

	private static async Task Read(CommandProcessorContext context, string[] args)
	{
		var stream = args.Length >= 1 ? args[0] : "test-stream";
		var eventNumber = args.Length >= 2 ? MetricPrefixValue.ParseLong(args[1]) : 0;
		if (eventNumber < -1)
		{
			throw new ArgumentOutOfRangeException(nameof(args), "Event number cannot be less than -1.");
		}

		var readLast = eventNumber == -1;
		var direction = readLast ? Direction.Backwards : Direction.Forwards;
		var start = readLast ? StreamPosition.End : StreamPosition.FromInt64(eventNumber);
		var requireLeader = args.Length >= 3 && bool.Parse(args[2]);
		var read = context._grpcTestClient.CreateGrpcClient(requireLeader).ReadStreamAsync(direction, stream, start,
			maxCount: 1, cancellationToken: context.CancellationToken);
		if (await read.ReadState != ReadState.Ok)
		{
			throw new InvalidOperationException($"Stream {stream} was not found.");
		}

		await foreach (var message in read.Messages.WithCancellation(context.CancellationToken))
		{
			if (message is not StreamMessage.Event observed)
			{
				continue;
			}

			context.Log.Information(
				"Read event {eventNumber} from {stream}",
				observed.ResolvedEvent.Event.EventNumber,
				stream);
			return;
		}

		throw new InvalidOperationException($"Event {eventNumber} was not found in stream {stream}.");
	}

	private static async Task ReadFlood(CommandProcessorContext context, string[] args)
	{
		var clients = args.Length == 0 ? 1 : MetricPrefixValue.ParseInt(args[0]);
		var requests = args.Length == 0 ? 5000 : MetricPrefixValue.ParseLong(args[1]);
		var streams = args.Length >= 3 ? MetricPrefixValue.ParseInt(args[2]) : 1;
		var prefix = args.Length >= 4 ? args[3] : "test-stream";
		var requireLeader = args.Length >= 5 && bool.Parse(args[4]);
		if (streams <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(args), "Stream count must be positive.");
		}

		var workload = FloodWorkload.Create(clients, requests, context._grpcTestClient.Options.ReadWindow);
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		long successes = 0;
		long failures = 0;
		await Task.WhenAll(Enumerable.Range(0, clients).Select(async clientIndex =>
		{
			var client = context._grpcTestClient.CreateGrpcClient(requireLeader);
			var streamIndex = streams / clients * clientIndex;
			await RunBounded(
				workload.RequestsForClient(clientIndex),
				workload.MaxInFlightPerClient,
				async () =>
				{
					if (await ReadOne(
						client,
						streams == 1 ? prefix : $"{prefix}-{streamIndex++ % streams}",
						context.CancellationToken))
					{
						Interlocked.Increment(ref successes);
					}
					else
					{
						Interlocked.Increment(ref failures);
					}
				});
		}));
		stopwatch.Stop();
		context.Log.Information(
			"Completed. READS succ: {successes}, fail: {failures}.",
			successes,
			failures);
		LogFloodCompletion(context, "RDFL", workload, stopwatch.Elapsed);
		if (successes != workload.RequestCount)
		{
			throw new InvalidOperationException("There were errors or not all requests completed.");
		}
	}

	private static async Task<bool> ReadOne(EventStoreClient client, string stream, CancellationToken cancellationToken)
	{
		var read = client.ReadStreamAsync(
			Direction.Forwards,
			stream,
			StreamPosition.Start,
			maxCount: 1,
			cancellationToken: cancellationToken);
		return await read.ReadState == ReadState.Ok;
	}

	private static async Task ReadAll(CommandProcessorContext context, string[] args)
	{
		var forwards = args.Length == 0 || args[0].Equals("F", StringComparison.OrdinalIgnoreCase);
		var position = forwards ? Position.Start : Position.End;
		if (args.Length >= 3)
		{
			var commitPosition = long.Parse(args[1]);
			var preparePosition = long.Parse(args[2]);
			if (commitPosition == -1 && preparePosition == -1)
			{
				position = Position.End;
			}
			else if (commitPosition < 0 || preparePosition < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(args), "Read-all positions must both be -1 or non-negative.");
			}
			else
			{
				position = new Position((ulong)commitPosition, (ulong)preparePosition);
			}
		}
		var requireLeader = args.Length >= 4 && bool.Parse(args[3]);

		var read = context._grpcTestClient.CreateGrpcClient(requireLeader).ReadAllAsync(
			forwards ? Direction.Forwards : Direction.Backwards,
			position,
			cancellationToken: context.CancellationToken);
		await foreach (var _ in read.Messages.WithCancellation(context.CancellationToken))
		{
		}
	}

	private static async Task WriteLongTerm(CommandProcessorContext context, string[] args)
	{
		var clients = args.Length == 0 ? 1 : MetricPrefixValue.ParseInt(args[0]);
		var minRate = args.Length == 0 ? 1 : MetricPrefixValue.ParseInt(args[1]);
		var maxRate = args.Length == 0 ? 2 : MetricPrefixValue.ParseInt(args[2]);
		var minutes = args.Length == 0 ? 1 : MetricPrefixValue.ParseInt(args[3]);
		var stream = args.Length == 5 ? args[4] : null;
		if (clients <= 0 || minRate <= 0 || maxRate < minRate || minutes < 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(args),
				"Clients and request rates must be positive, max rate cannot be below min rate, and duration cannot be negative.");
		}

		using var duration = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
		duration.CancelAfter(TimeSpan.FromMinutes(minutes));
		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		long successes = 0;
		long failures = 0;
		var maxInFlight = Math.Max(1, context._grpcTestClient.Options.WriteWindow / clients);
		await Task.WhenAll(Enumerable.Range(0, clients).Select(async clientIndex =>
		{
			var client = context._grpcTestClient.CreateGrpcClient();
			var pending = new List<Task>(maxInFlight);
			while (!duration.IsCancellationRequested)
			{
				var rate = minRate == maxRate ? minRate : Random.Shared.Next(minRate, maxRate + 1);
				var interval = TimeSpan.FromSeconds(1d / rate);
				for (var i = 0; i < rate; i++)
				{
					if (duration.IsCancellationRequested)
					{
						break;
					}

					var dataSize = Random.Shared.Next(8, 256) * 8;
					pending.Add(RecordWrite(
						client,
						stream ?? $"Stream-{clientIndex % 3}",
						Event(
							$"DATA {dataSize:00000} {new string('*', dataSize)}",
							"METADATA" + new string('$', 100)),
						context.CancellationToken));
					if (pending.Count >= maxInFlight)
					{
						await DrainCompleted(pending);
					}

					try
					{
						await Task.Delay(interval, duration.Token);
					}
					catch (OperationCanceledException) when (duration.IsCancellationRequested)
					{
						break;
					}
				}
			}

			await Task.WhenAll(pending);
		}));
		stopwatch.Stop();
		var requestCount = successes + failures;
		var ratePerSecond = stopwatch.ElapsedMilliseconds == 0
			? 0
			: 1000d * requestCount / stopwatch.ElapsedMilliseconds;
		context.Log.Information(
			"Completed. Successes: {successes}, failures: {failures}. {requests} requests completed in {elapsed}ms ({rate:0.00} reqs per sec).",
			successes,
			failures,
			requestCount,
			stopwatch.ElapsedMilliseconds,
			ratePerSecond);
		PerfUtils.LogData(
			"WRLT",
			PerfUtils.Row(
				PerfUtils.Col("clientsCnt", clients),
				PerfUtils.Col("requestsCnt", requestCount),
				PerfUtils.Col("ElapsedMilliseconds", stopwatch.ElapsedMilliseconds)),
			PerfUtils.Row(PerfUtils.Col("successes", successes), PerfUtils.Col("failures", failures)));
		PerfUtils.LogTeamCityGraphData($"WRLT-{clients}-{requestCount}-reqPerSec", (int)ratePerSecond);
		if (requestCount > 0)
		{
			PerfUtils.LogTeamCityGraphData(
				$"WRLT-{clients}-{requestCount}-failureSuccessRate",
				(int)(100 * failures / requestCount));
		}

		if (failures > 0)
		{
			throw new InvalidOperationException($"{failures} long-term write requests failed.");
		}

		async Task RecordWrite(
			EventStoreClient client,
			string targetStream,
			EventData eventData,
			CancellationToken cancellationToken)
		{
			try
			{
				await client.AppendToStreamAsync(
					targetStream,
					StreamState.Any,
					[eventData],
					cancellationToken: cancellationToken);
				Interlocked.Increment(ref successes);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
				Interlocked.Increment(ref failures);
			}
		}

		static async Task DrainCompleted(List<Task> pending)
		{
			await Task.WhenAny(pending);
			for (var index = pending.Count - 1; index >= 0; index--)
			{
				if (pending[index].IsCompleted)
				{
					await pending[index];
					pending.RemoveAt(index);
				}
			}
		}
	}

	private static async Task Subscribe(CommandProcessorContext context, string[] args)
	{
		var client = context._grpcTestClient.CreateGrpcClient();
		var subscriptions = new List<StreamSubscription>();

		if (args.Length == 0)
		{
			context.Log.Information("Subscribing to all streams over gRPC");
			subscriptions.Add(await client.SubscribeToAllAsync(
				FromAll.End,
				(_, resolvedEvent, _) => LogSubscriptionEvent(context, resolvedEvent),
				cancellationToken: context.CancellationToken));
		}
		else
		{
			foreach (var stream in args)
			{
				context.Log.Information("Subscribing to stream {stream} over gRPC", stream);
				subscriptions.Add(await client.SubscribeToStreamAsync(
					stream,
					FromStream.End,
					(_, resolvedEvent, _) => LogSubscriptionEvent(context, resolvedEvent),
					cancellationToken: context.CancellationToken));
			}
		}

		context.Log.Information("Subscribed to {subscriptionCount} streams over gRPC", subscriptions.Count);
		await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.CancellationToken);
	}

	private static async Task SubscriptionStress(CommandProcessorContext context, string[] args)
	{
		var count = args.Length == 0 ? 5000 : MetricPrefixValue.ParseInt(args[0]);
		var client = context._grpcTestClient.CreateGrpcClient();
		var subscriptions = new List<StreamSubscription>(count);
		long appeared = 0;
		var interval = System.Diagnostics.Stopwatch.StartNew();
		for (var i = 0; i < count; i++)
		{
			subscriptions.Add(await client.SubscribeToStreamAsync($"stream-{i}", FromStream.End,
				(_, _, _) =>
				{
					var observed = Interlocked.Increment(ref appeared);
					if (observed % 100000 == 0)
					{
						context.Log.Information(
							"Received {eventCount} subscription events at {rate:0.0} events per second",
							observed,
							100000 / interval.Elapsed.TotalSeconds);
						interval.Restart();
					}

					return Task.CompletedTask;
				}, cancellationToken: context.CancellationToken));
		}

		context.Log.Information("Subscribed to {subscriptionCount} streams over gRPC", subscriptions.Count);
		await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.CancellationToken);
	}

	private static Task LogSubscriptionEvent(CommandProcessorContext context, ResolvedEvent resolvedEvent)
	{
		var @event = resolvedEvent.Event;
		context.Log.Information(
			"New event on {stream} at {eventNumber}: {eventType}\nData: {data}\nMetadata: {metadata}",
			@event.EventStreamId,
			@event.EventNumber,
			@event.EventType,
			Utf8NoBom.GetString(@event.Data.Span),
			Utf8NoBom.GetString(@event.Metadata.Span));
		return Task.CompletedTask;
	}

	private static async Task Scavenge(CommandProcessorContext context, string[] args)
	{
		var result = await context._grpcTestClient.CreateOperationsClient()
			.StartScavengeAsync(cancellationToken: context.CancellationToken);
		context.Log.Information("Scavenge request returned {result}", result);
	}

	private static async Task Verify(CommandProcessorContext context, string[] args)
	{
		var writers = args.Length == 0 ? 20 : MetricPrefixValue.ParseInt(args[0]);
		var readers = args.Length == 0 ? 30 : MetricPrefixValue.ParseInt(args[1]);
		var events = args.Length == 0 ? 1_000_000 : MetricPrefixValue.ParseInt(args[2]);
		var streams = args.Length == 0 ? 1_000 : MetricPrefixValue.ParseInt(args[3]);
		var producers = args.Length == 0 ? ["bank"] : args[4]
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (writers <= 0 || readers <= 0 || events < 0 || streams <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(args), "Writers, readers, and streams must be positive, and events cannot be negative.");
		}

		if (producers.Length != 1 || !producers[0].Equals("bank", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The gRPC verification workload supports the bank producer.", nameof(args));
		}

		var heads = Enumerable.Repeat(-1, streams).ToArray();
		var streamGates = Enumerable.Range(0, streams).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
		var stopReading = false;
		try
		{
			var writerTasks = Enumerable.Range(0, writers).Select(async writerIndex =>
			{
				var client = context._grpcTestClient.CreateGrpcClient();
				var random = new Random(writerIndex);
				var requests = events / writers + (writerIndex == writers - 1 ? events % writers : 0);
				for (var request = 0; request < requests; request++)
				{
					var streamIndex = writers >= streams ? writerIndex % streams : random.Next(streams);
					await streamGates[streamIndex].WaitAsync(context.CancellationToken);
					try
					{
						var nextVersion = heads[streamIndex] + 1;
						var expected = nextVersion == 0
							? new ExpectedRevision(StreamState.NoStream, null)
							: new ExpectedRevision(default, StreamRevision.FromInt64(nextVersion - 1));
						await Append(
							client,
							$"account-{streamIndex}",
							expected,
							[VerificationEventFactory.Create(nextVersion)],
							null,
							context.CancellationToken);
						Volatile.Write(ref heads[streamIndex], nextVersion);
					}
					finally
					{
						streamGates[streamIndex].Release();
					}
				}
			}).ToArray();

			var readerTasks = Enumerable.Range(0, readers).Select(async readerIndex =>
			{
				var client = context._grpcTestClient.CreateGrpcClient();
				var random = new Random(readerIndex);
				while (!Volatile.Read(ref stopReading))
				{
					var streamIndex = random.Next(streams);
					var head = Volatile.Read(ref heads[streamIndex]);
					if (head < 0)
					{
						await Task.Yield();
						continue;
					}

					await VerifyEventAt(
						client,
						$"account-{streamIndex}",
						random.Next(head + 1),
						context.CancellationToken);
				}
			}).ToArray();

			try
			{
				await Task.WhenAll(writerTasks);
			}
			finally
			{
				Volatile.Write(ref stopReading, true);
				await Task.WhenAll(readerTasks);
			}

			var verificationClient = context._grpcTestClient.CreateGrpcClient();
			for (var streamIndex = 0; streamIndex < streams; streamIndex++)
			{
				for (var eventNumber = 0; eventNumber <= heads[streamIndex]; eventNumber++)
				{
					await VerifyEventAt(
						verificationClient,
						$"account-{streamIndex}",
						eventNumber,
						context.CancellationToken);
				}
			}
		}
		finally
		{
			foreach (var gate in streamGates)
			{
				gate.Dispose();
			}
		}
	}

	private static async Task VerifyEventAt(
		EventStoreClient client,
		string stream,
		int eventNumber,
		CancellationToken cancellationToken)
	{
		var read = client.ReadStreamAsync(
			Direction.Forwards,
			stream,
			StreamPosition.FromInt64(eventNumber),
			maxCount: 1,
			cancellationToken: cancellationToken);
		await foreach (var message in read.Messages.WithCancellation(cancellationToken))
		{
			if (message is not StreamMessage.Event observed)
			{
				continue;
			}

			var expected = VerificationEventFactory.Create(eventNumber);
			if (observed.ResolvedEvent.Event.EventType != expected.Type ||
				!observed.ResolvedEvent.Event.Data.Span.SequenceEqual(expected.Data.Span))
			{
				throw new InvalidOperationException($"Event {eventNumber} in {stream} did not match its expected payload.");
			}

			return;
		}

		throw new InvalidOperationException($"Event {eventNumber} was not found in {stream}.");
	}

	private static async Task CheckGrpcSanitization(CommandProcessorContext context, string[] args)
	{
		using var handler = new HttpClientHandler();
		handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
		using var client = new HttpClient(handler);
		using var request = new HttpRequestMessage(HttpMethod.Post, context._grpcTestClient.HttpEndpoint)
		{
			Version = HttpVersion.Version20,
			VersionPolicy = HttpVersionPolicy.RequestVersionExact,
			Content = new ByteArrayContent([0, 0, 0, 0, 16, 1])
		};
		request.RequestUri = new Uri(request.RequestUri, "/event_store.client.streams.Streams/Read");
		request.Content.Headers.ContentType = new("application/grpc");
		using var response = await client.SendAsync(request, context.CancellationToken);
		await response.Content.ReadAsByteArrayAsync(context.CancellationToken);
		var grpcStatus = response.TrailingHeaders.TryGetValues("grpc-status", out var trailerValues)
			? trailerValues.SingleOrDefault()
			: response.Headers.TryGetValues("grpc-status", out var headerValues)
				? headerValues.SingleOrDefault()
				: null;
		if (response.IsSuccessStatusCode && (grpcStatus is null or "0"))
		{
			throw new InvalidOperationException("The server accepted a malformed gRPC frame.");
		}
	}

	private readonly record struct ExpectedRevision(StreamState State, StreamRevision? Revision)
	{
		public static ExpectedRevision Parse(string value) => value.ToUpperInvariant() switch
		{
			"ANY" or "-2" => new(StreamState.Any, null),
			"NO_STREAM" or "NOSTREAM" or "-1" => new(StreamState.NoStream, null),
			_ => new(default, StreamRevision.FromInt64(long.Parse(value)))
		};
	}
}

internal sealed class DelegatingProcessor : ICmdProcessor
{
	private readonly ICmdProcessor _inner;
	private readonly Func<string[], string[]> _translate;

	public DelegatingProcessor(string keyword, string usage, ICmdProcessor inner, Func<string[], string[]> translate = null)
	{
		Keyword = keyword;
		Usage = usage;
		_inner = inner;
		_translate = translate ?? (args => args);
	}

	public string Keyword { get; }
	public string Usage { get; }

	public bool Execute(CommandProcessorContext context, string[] args) => _inner.Execute(context, _translate(args));
}
