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
public class subscribe_should<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	protected override Task Given() => Task.CompletedTask;

	protected override Task When() => Task.CompletedTask;

	[Test]
	public async Task catch_deleted_events_as_well()
	{
		const string streamName = nameof(catch_deleted_events_as_well);
		using var subscription = StreamsClient.Read(new()
		{
			Options = new()
			{
				Subscription = new(),
				NoFilter = new(),
				ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
				UuidOption = new() { Structured = new() },
				Stream = new()
				{
					End = new(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
				}
			}
		}, GetCallOptions(AdminCredentials));

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

		ReadResp deleted = null;
		while (await subscription.ResponseStream.MoveNext())
		{
			if (subscription.ResponseStream.Current.ContentCase == ReadResp.ContentOneofCase.Event)
			{
				deleted = subscription.ResponseStream.Current;
				break;
			}
		}

		Assert.That(deleted, Is.Not.Null);
		Assert.That(deleted.Event.Event.StreamIdentifier.StreamName.ToStringUtf8(), Is.EqualTo(streamName));
		Assert.That(deleted.Event.Event.Metadata[GrpcMetadata.Type], Is.EqualTo(SystemEventTypes.StreamDeleted));
		Assert.That(deleted.Event.Event.StreamRevision, Is.EqualTo((ulong)long.MaxValue));
	}
}
