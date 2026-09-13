using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

public abstract class FilteredReadAllSpecification<TLogFormat, TStreamId>
	: GrpcSpecification<TLogFormat, TStreamId>
{
	protected const string StreamA = "stream-a";
	protected const string StreamB = "stream-b";

	protected override async Task Given()
	{
		await Append(StreamA, Enumerable.Range(0, 10)
			.Select(i => CreateEvent(i % 2 == 0 ? "AEvent" : "BEvent")));
		await Append(StreamB, Enumerable.Range(0, 10)
			.Select(i => CreateEvent(i % 2 == 0 ? "BEvent" : "AEvent")));
	}

	protected override Task When() => Task.CompletedTask;

	protected async Task<ReadResp.Types.ReadEvent[]> Read(
		ReadReq.Types.Options.Types.ReadDirection direction,
		ReadReq.Types.Options.Types.FilterOptions filter)
	{
		using var call = StreamsClient.Read(new()
		{
			Options = new()
			{
				UuidOption = new() { Structured = new() },
				Count = 1000,
				ReadDirection = direction,
				ResolveLinks = false,
				All = direction == ReadReq.Types.Options.Types.ReadDirection.Forwards
					? new() { Start = new() }
					: new() { End = new() },
				Filter = filter
			}
		}, GetCallOptions(AdminCredentials));

		return (await call.ResponseStream.ReadAllAsync().ToArrayAsync())
			.Where(x => x.ContentCase == ReadResp.ContentOneofCase.Event)
			.Select(x => x.Event)
			.ToArray();
	}

	protected static ReadReq.Types.Options.Types.FilterOptions StreamPrefix(string prefix) => new()
	{
		Count = new(),
		CheckpointIntervalMultiplier = 1,
		StreamIdentifier = new() { Prefix = { prefix } }
	};

	protected static ReadReq.Types.Options.Types.FilterOptions EventTypePrefix(string prefix) => new()
	{
		Count = new(),
		CheckpointIntervalMultiplier = 1,
		EventType = new() { Prefix = { prefix } }
	};

	protected static ReadReq.Types.Options.Types.FilterOptions StreamRegex(string regex) => new()
	{
		Count = new(),
		CheckpointIntervalMultiplier = 1,
		StreamIdentifier = new() { Regex = regex }
	};

	protected static ReadReq.Types.Options.Types.FilterOptions EventTypeRegex(string regex) => new()
	{
		Count = new(),
		CheckpointIntervalMultiplier = 1,
		EventType = new() { Regex = regex }
	};

	protected static string StreamName(ReadResp.Types.ReadEvent readEvent) =>
		readEvent.Event.StreamIdentifier.StreamName.ToStringUtf8();

	protected static string EventType(ReadResp.Types.ReadEvent readEvent) =>
		readEvent.Event.Metadata[GrpcMetadata.Type];

	private ValueTask<BatchAppendResp> Append(
		string streamName,
		IEnumerable<BatchAppendReq.Types.ProposedMessage> events) =>
		AppendToStreamBatch(new BatchAppendReq
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
			},
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			ProposedMessages = { events }
		});
}
