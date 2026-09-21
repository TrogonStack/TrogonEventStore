using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using GrpcConstants = EventStore.Core.Services.Transport.Grpc.Constants;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class read_all_events_forward_with_hard_deleted_stream_should<TLogFormat, TStreamId>
	: GrpcSpecification<TLogFormat, TStreamId>
{
	private const string StreamName = nameof(read_all_events_forward_with_hard_deleted_stream_should<TLogFormat, TStreamId>);
	private readonly List<ReadResp.Types.ReadEvent> _allEvents = new();
	private readonly List<ReadResp.Types.ReadEvent> _allEventsBackward = new();
	private RpcException _streamReadException;

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

		await StreamsClient.TombstoneAsync(new()
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
		try
		{
			using var streamCall = StreamsClient.Read(ReadStreamRequest(), GetCallOptions(AdminCredentials));
			await streamCall.ResponseStream.ReadAllAsync().ToArrayAsync();
		}
		catch (RpcException ex)
		{
			_streamReadException = ex;
		}

		using var allCall = StreamsClient.Read(ReadAllRequest(
			ReadReq.Types.Options.Types.ReadDirection.Forwards), GetCallOptions(AdminCredentials));
		_allEvents.AddRange((await allCall.ResponseStream.ReadAllAsync().ToArrayAsync())
			.Where(x => x.ContentCase == ReadResp.ContentOneofCase.Event)
			.Select(x => x.Event));

		using var allBackwardCall = StreamsClient.Read(ReadAllRequest(
			ReadReq.Types.Options.Types.ReadDirection.Backwards), GetCallOptions(AdminCredentials));
		_allEventsBackward.AddRange((await allBackwardCall.ResponseStream.ReadAllAsync().ToArrayAsync())
			.Where(x => x.ContentCase == ReadResp.ContentOneofCase.Event)
			.Select(x => x.Event));
	}

	[Test]
	public void ensure_deleted_stream()
	{
		Assert.That(_streamReadException, Is.Not.Null);
		Assert.That(_streamReadException.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
		Assert.That(_streamReadException.Trailers.Select(x => (x.Key, x.Value)),
			Does.Contain((GrpcConstants.Exceptions.ExceptionKey, GrpcConstants.Exceptions.StreamDeleted)));
	}

	[Test]
	public void returns_all_events_including_tombstone()
	{
		var streamEvents = _allEvents
			.Where(x => x.Event.StreamIdentifier.StreamName.ToStringUtf8() == StreamName)
			.ToArray();

		Assert.That(streamEvents, Has.Length.EqualTo(21));
		Assert.That(streamEvents.Take(20).All(x => x.Event.Metadata[GrpcConstants.Metadata.Type] == "-"), Is.True);
		Assert.That(streamEvents[^1].Event.Metadata[GrpcConstants.Metadata.Type],
			Is.EqualTo(SystemEventTypes.StreamDeleted));
		Assert.That(streamEvents[^1].Event.StreamRevision, Is.EqualTo((ulong)long.MaxValue));
	}

	[Test]
	public void returns_tombstone_from_backward_read_without_downgrading_its_revision()
	{
		var streamEvents = _allEventsBackward
			.Where(x => x.Event.StreamIdentifier.StreamName.ToStringUtf8() == StreamName)
			.ToArray();

		Assert.That(streamEvents, Has.Length.EqualTo(21));
		Assert.That(streamEvents[0].Event.Metadata[GrpcConstants.Metadata.Type],
			Is.EqualTo(SystemEventTypes.StreamDeleted));
		Assert.That(streamEvents[0].Event.StreamRevision, Is.EqualTo((ulong)long.MaxValue));
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

	private static ReadReq ReadAllRequest(ReadReq.Types.Options.Types.ReadDirection direction) => new()
	{
		Options = new()
		{
			UuidOption = new() { Structured = new() },
			NoFilter = new(),
			ReadDirection = direction,
			Count = 100,
			All = direction == ReadReq.Types.Options.Types.ReadDirection.Forwards
				? new() { Start = new() }
				: new() { End = new() }
		}
	};
}
