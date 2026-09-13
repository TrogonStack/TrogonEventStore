using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.TransactionLog.LogRecords;
using Google.Protobuf;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class isjson_flag_on_event<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	private const string StreamName = nameof(isjson_flag_on_event<TLogFormat, TStreamId>);
	private bool _allEventsAreJson;

	protected override async Task Given()
	{
		var json = ByteString.CopyFromUtf8("{\"some\":\"json\"}");
		var response = await AppendToStreamBatch(new BatchAppendReq
		{
			Options = new()
			{
				Any = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamName) }
			},
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			ProposedMessages =
			{
				CreateJsonEvent(json, ByteString.Empty),
				CreateJsonEvent(ByteString.Empty, json),
				CreateJsonEvent(json, json)
			}
		});

		Assert.That(response.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
	}

	protected override async Task When()
	{
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		Node.Node.MainQueue.Publish(new ClientMessage.ReadStreamEventsForward(
			Guid.NewGuid(),
			Guid.NewGuid(),
			new CallbackEnvelope(message =>
			{
				if (message is not ClientMessage.ReadStreamEventsForwardCompleted completed)
				{
					completion.TrySetException(new InvalidOperationException(
						$"Unexpected response type {message.GetType().Name}."));
					return;
				}

				completion.TrySetResult(
					completed.Result == Data.ReadStreamResult.Success &&
					completed.Events.Count == 3 &&
					completed.Events.All(x => (x.OriginalEvent.Flags & PrepareFlags.IsJson) != 0));
			}),
			StreamName,
			0,
			100,
			false,
			false,
			null,
			null,
			replyOnExpired: false));

		_allEventsAreJson = await completion.Task.WithTimeout(TimeSpan.FromSeconds(10));
	}

	[Test]
	public void should_be_preserved_with_all_possible_write_and_read_methods() =>
		Assert.That(_allEventsAreJson, Is.True);

	private static BatchAppendReq.Types.ProposedMessage CreateJsonEvent(ByteString data, ByteString customMetadata) =>
		new()
		{
			Id = Uuid.NewUuid().ToDto(),
			Metadata =
			{
				[GrpcMetadata.Type] = "some-type",
				[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson
			},
			Data = data,
			CustomMetadata = customMetadata
		};
}
