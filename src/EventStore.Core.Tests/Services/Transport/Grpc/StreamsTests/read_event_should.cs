using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using GrpcConstants = EventStore.Core.Services.Transport.Grpc.Constants;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class read_event_should<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	private const string StreamName = "test-stream";
	private const string DeletedStreamName = "deleted-stream";
	private readonly Uuid _eventId0 = Uuid.NewUuid();
	private readonly Uuid _eventId1 = Uuid.NewUuid();

	protected override async Task Given()
	{
		await AppendToStreamBatch(new BatchAppendReq
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamName) }
			},
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			ProposedMessages =
			{
				CreateEvent(_eventId0, "event0", GrpcConstants.Metadata.ContentTypes.ApplicationOctetStream),
				CreateEvent(_eventId1, "event1", GrpcConstants.Metadata.ContentTypes.ApplicationJson)
			}
		});

		await StreamsClient.TombstoneAsync(new()
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(DeletedStreamName) }
			}
		}, GetCallOptions(AdminCredentials));
	}

	protected override Task When() => Task.CompletedTask;

	[Test]
	public async Task notify_using_status_code_if_stream_not_found()
	{
		var responses = await Read("unexisting-stream", revision: 5);

		Assert.That(responses, Has.Length.EqualTo(1));
		Assert.That(responses[0].ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.StreamNotFound));
		Assert.That(responses[0].StreamNotFound.StreamIdentifier.StreamName.ToStringUtf8(),
			Is.EqualTo("unexisting-stream"));
	}

	[Test]
	public async Task return_no_stream_if_requested_last_event_in_empty_stream()
	{
		var responses = await Read("some-really-empty-stream", fromEnd: true);

		Assert.That(responses, Has.Length.EqualTo(1));
		Assert.That(responses[0].ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.StreamNotFound));
	}

	[Test]
	public void notify_using_status_code_if_stream_was_deleted()
	{
		var exception = Assert.ThrowsAsync<RpcException>(async () => await Read(DeletedStreamName, revision: 5));

		Assert.That(exception.StatusCode, Is.EqualTo(StatusCode.FailedPrecondition));
		Assert.That(exception.Trailers.Select(x => (x.Key, x.Value)),
			Does.Contain((GrpcConstants.Exceptions.ExceptionKey, GrpcConstants.Exceptions.StreamDeleted)));
	}

	[Test]
	public async Task notify_using_status_code_if_stream_does_not_have_event()
	{
		var responses = await Read(StreamName, revision: 5);

		Assert.That(responses.Count(x => x.ContentCase == ReadResp.ContentOneofCase.Event), Is.Zero);
	}

	[Test]
	public async Task return_existing_event()
	{
		var readEvent = (await Read(StreamName, revision: 0))
			.Single(x => x.ContentCase == ReadResp.ContentOneofCase.Event).Event;

		Assert.That(Uuid.FromDto(readEvent.Event.Id), Is.EqualTo(_eventId0));
		Assert.That(readEvent.Event.StreamIdentifier.StreamName.ToStringUtf8(), Is.EqualTo(StreamName));
		Assert.That(readEvent.Event.StreamRevision, Is.Zero);
		Assert.That(readEvent.Event.Metadata[GrpcConstants.Metadata.Created], Is.Not.Empty);
	}

	[Test]
	public async Task retrieve_the_is_json_flag_properly()
	{
		var readEvent = (await Read(StreamName, revision: 1))
			.Single(x => x.ContentCase == ReadResp.ContentOneofCase.Event).Event;

		Assert.That(Uuid.FromDto(readEvent.Event.Id), Is.EqualTo(_eventId1));
		Assert.That(readEvent.Event.Metadata[GrpcConstants.Metadata.ContentType],
			Is.EqualTo(GrpcConstants.Metadata.ContentTypes.ApplicationJson));
	}

	[Test]
	public async Task return_last_event_in_stream_if_event_number_is_minus_one()
	{
		var readEvent = (await Read(StreamName, fromEnd: true))
			.Single(x => x.ContentCase == ReadResp.ContentOneofCase.Event).Event;

		Assert.That(Uuid.FromDto(readEvent.Event.Id), Is.EqualTo(_eventId1));
		Assert.That(readEvent.Event.StreamIdentifier.StreamName.ToStringUtf8(), Is.EqualTo(StreamName));
		Assert.That(readEvent.Event.StreamRevision, Is.EqualTo(1));
		Assert.That(readEvent.Event.Metadata[GrpcConstants.Metadata.Created], Is.Not.Empty);
	}

	private async Task<ReadResp[]> Read(string streamName, ulong revision = 0, bool fromEnd = false)
	{
		var request = new ReadReq
		{
			Options = new()
			{
				UuidOption = new() { Structured = new() },
				NoFilter = new(),
				ReadDirection = fromEnd
					? ReadReq.Types.Options.Types.ReadDirection.Backwards
					: ReadReq.Types.Options.Types.ReadDirection.Forwards,
				Count = 1,
				Stream = new()
				{
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
				}
			}
		};

		if (fromEnd)
		{
			request.Options.Stream.End = new();
		}
		else
		{
			request.Options.Stream.Revision = revision;
		}

		using var call = StreamsClient.Read(request, GetCallOptions(AdminCredentials));
		return await call.ResponseStream.ReadAllAsync().ToArrayAsync();
	}

	private static BatchAppendReq.Types.ProposedMessage CreateEvent(Uuid id, string eventType, string contentType) =>
		new()
		{
			Id = id.ToDto(),
			Metadata =
			{
				[GrpcConstants.Metadata.Type] = eventType,
				[GrpcConstants.Metadata.ContentType] = contentType
			},
			Data = ByteString.Empty,
			CustomMetadata = ByteString.Empty
		};
}
