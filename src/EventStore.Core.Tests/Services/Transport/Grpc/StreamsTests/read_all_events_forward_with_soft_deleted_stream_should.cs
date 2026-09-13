using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Data;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using GrpcConstants = EventStore.Core.Services.Transport.Grpc.Constants;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class read_all_events_forward_with_soft_deleted_stream_should<TLogFormat, TStreamId>
	: GrpcSpecification<TLogFormat, TStreamId>
{
	private const string StreamName = nameof(read_all_events_forward_with_soft_deleted_stream_should<TLogFormat, TStreamId>);
	private readonly List<ReadResp> _streamResponses = new();
	private readonly List<ReadResp.Types.ReadEvent> _allEvents = new();

	protected override async Task Given()
	{
		var appendResponse = await AppendToStreamBatch(new BatchAppendReq
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamName) }
			},
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			ProposedMessages = { CreateEvents(20) }
		});
		Assert.That(appendResponse.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));

		await StreamsClient.DeleteAsync(new()
		{
			Options = new()
			{
				Any = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamName) }
			}
		}, GetCallOptions(AdminCredentials));
	}

	protected override async Task When()
	{
		using (var streamCall = StreamsClient.Read(ReadStreamRequest(), GetCallOptions(AdminCredentials)))
		{
			_streamResponses.AddRange(await streamCall.ResponseStream.ReadAllAsync().ToArrayAsync());
		}

		using var allCall = StreamsClient.Read(ReadAllRequest(), GetCallOptions(AdminCredentials));
		_allEvents.AddRange((await allCall.ResponseStream.ReadAllAsync().ToArrayAsync())
			.Where(x => x.ContentCase == ReadResp.ContentOneofCase.Event)
			.Select(x => x.Event));
	}

	[Test]
	public void ensure_deleted_stream()
	{
		Assert.That(_streamResponses.Count(x => x.ContentCase == ReadResp.ContentOneofCase.Event), Is.Zero);
		Assert.That(_streamResponses.Single().ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.StreamNotFound));
	}

	[Test]
	public void returns_all_events_including_tombstone()
	{
		var streamEvents = _allEvents
			.Where(x => x.Event.StreamIdentifier.StreamName.ToStringUtf8() == StreamName)
			.ToArray();
		Assert.That(streamEvents, Has.Length.EqualTo(20));
		Assert.That(streamEvents.All(x => x.Event.Metadata[GrpcConstants.Metadata.Type] == "-"), Is.True);

		var metadataEvent = _allEvents.Single(x =>
			x.Event.StreamIdentifier.StreamName.ToStringUtf8() == SystemStreams.MetastreamOf(StreamName));
		Assert.That(metadataEvent.Event.Metadata[GrpcConstants.Metadata.Type], Is.EqualTo(SystemEventTypes.StreamMetadata));
		Assert.That(StreamMetadata.FromJsonBytes(metadataEvent.Event.Data.Memory).TruncateBefore,
			Is.EqualTo(EventNumber.DeletedStream));
	}

	private static ReadReq ReadStreamRequest() => new()
	{
		Options = new()
		{
			UuidOption = new() { Structured = new() },
			NoFilter = new(),
			ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
			Count = 100,
			Stream = new()
			{
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamName) },
				Start = new()
			}
		}
	};

	private static ReadReq ReadAllRequest() => new()
	{
		Options = new()
		{
			UuidOption = new() { Structured = new() },
			NoFilter = new(),
			ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
			Count = 100,
			All = new() { Start = new() }
		}
	};
}
