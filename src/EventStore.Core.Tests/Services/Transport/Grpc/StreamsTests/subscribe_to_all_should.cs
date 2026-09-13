using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class subscribe_to_all_should<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	protected override Task Given() => Task.CompletedTask;

	protected override Task When() => Task.CompletedTask;

	[Test]
	public async Task allow_multiple_subscriptions()
	{
		const string streamName = nameof(allow_multiple_subscriptions);
		using var first = StreamsClient.Read(SubscribeRequest(), GetCallOptions(AdminCredentials));
		using var second = StreamsClient.Read(SubscribeRequest(), GetCallOptions(AdminCredentials));

		Assert.That(await first.ResponseStream.MoveNext(), Is.True);
		Assert.That(first.ResponseStream.Current.ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.Confirmation));
		Assert.That(await second.ResponseStream.MoveNext(), Is.True);
		Assert.That(second.ResponseStream.Current.ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.Confirmation));

		await AppendToStreamBatch(new BatchAppendReq
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
			},
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			ProposedMessages = { CreateEvents(1) }
		});

		Assert.That(await ReadNextEvent(first), Is.EqualTo(streamName));
		Assert.That(await ReadNextEvent(second), Is.EqualTo(streamName));
	}

	[Test]
	public async Task catch_deleted_events_as_well()
	{
		const string streamName = nameof(catch_deleted_events_as_well);
		using var subscription = StreamsClient.Read(SubscribeRequest(), GetCallOptions(AdminCredentials));
		Assert.That(await subscription.ResponseStream.MoveNext(), Is.True);
		Assert.That(subscription.ResponseStream.Current.ContentCase,
			Is.EqualTo(ReadResp.ContentOneofCase.Confirmation));

		await StreamsClient.TombstoneAsync(new()
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
			}
		}, GetCallOptions(AdminCredentials));

		var deleted = await ReadNextEventResponse(subscription);
		Assert.That(deleted.Event.StreamIdentifier.StreamName.ToStringUtf8(), Is.EqualTo(streamName));
		Assert.That(deleted.Event.Metadata[GrpcMetadata.Type], Is.EqualTo(SystemEventTypes.StreamDeleted));
	}

	private static ReadReq SubscribeRequest() => new()
	{
		Options = new()
		{
			Subscription = new(),
			NoFilter = new(),
			ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
			UuidOption = new() { Structured = new() },
			All = new() { End = new() }
		}
	};

	private static async Task<string> ReadNextEvent(global::Grpc.Core.AsyncServerStreamingCall<ReadResp> subscription) =>
		(await ReadNextEventResponse(subscription)).Event.StreamIdentifier.StreamName.ToStringUtf8();

	private static async Task<ReadResp.Types.ReadEvent> ReadNextEventResponse(
		global::Grpc.Core.AsyncServerStreamingCall<ReadResp> subscription)
	{
		while (await subscription.ResponseStream.MoveNext())
		{
			if (subscription.ResponseStream.Current.ContentCase == ReadResp.ContentOneofCase.Event)
			{
				return subscription.ResponseStream.Current.Event;
			}
		}

		throw new AssertionException("The subscription ended before an event was received.");
	}
}
