using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Client.Streams;
using EventStore.Core.Data;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using GrpcMetadata = EventStore.Core.Services.Transport.Grpc.Constants.Metadata;
using ReadReq = EventStore.Client.PersistentSubscriptions.ReadReq;
using ReadResp = EventStore.Client.PersistentSubscriptions.ReadResp;
using Streams = EventStore.Client.Streams.Streams;

namespace EventStore.Core.Tests.Services.Transport.Grpc.PersistentSubscriptionTests;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class PersistentSubscriptionWithEventNumbersGreaterThan2BillionTests<TLogFormat, TStreamId>
	: GrpcSpecificationWithExistingRecords<TLogFormat, TStreamId>
{
	private const long IntMaxValue = int.MaxValue;
	private const string StreamName = "persistent-subscription-stream";
	private EventRecord _first;
	private EventRecord _second;
	private PersistentSubscriptions.PersistentSubscriptionsClient _persistentSubscriptions;
	private Streams.StreamsClient _streams;

	public override async ValueTask WriteTestScenario(CancellationToken token)
	{
		_first = await WriteSingleEvent(StreamName, IntMaxValue + 1, "first", token: token);
		_second = await WriteSingleEvent(StreamName, IntMaxValue + 2, "second", token: token);
	}

	public override async Task Given()
	{
		_persistentSubscriptions = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);
		_streams = new Streams.StreamsClient(Channel);
		await Append(
			"$$" + StreamName,
			new[] { ProposedEvent("$metadata", "{\"$tb\":2147483648}") });
	}

	[Test]
	public async Task can_create_a_subscription_above_int_max_value()
	{
		await _persistentSubscriptions.CreateAsync(
			CreateRequest(NewName("group"), (ulong)IntMaxValue),
			GetCallOptions(AdminCredentials));
	}

	[Test]
	public async Task can_update_a_subscription_above_int_max_value()
	{
		var groupName = NewName("group");
		await _persistentSubscriptions.CreateAsync(
			CreateRequest(groupName, 0), GetCallOptions(AdminCredentials));

		await _persistentSubscriptions.UpdateAsync(
			UpdateRequest(groupName, (ulong)IntMaxValue),
			GetCallOptions(AdminCredentials));
	}

	[Test]
	public async Task delivers_and_appends_events_above_int_max_value()
	{
		var groupName = NewName("group");
		var thirdId = Guid.NewGuid();
		await _persistentSubscriptions.CreateAsync(
			CreateRequest(groupName, (ulong)IntMaxValue),
			GetCallOptions(AdminCredentials));
		using var subscription = await Subscribe(groupName);

		await Append(
			StreamName,
			new[] { ProposedEvent("third", "third", thirdId) },
			(ulong)(IntMaxValue + 2));

		var expected = new[]
		{
			(_first.EventId, (ulong)(IntMaxValue + 1)),
			(_second.EventId, (ulong)(IntMaxValue + 2)),
			(thirdId, (ulong)(IntMaxValue + 3))
		};
		var actual = new List<(Guid eventId, ulong revision)>();
		for (var index = 0; index < expected.Length; index++)
		{
			Assert.True(await subscription.ResponseStream.MoveNext());
			var response = subscription.ResponseStream.Current;
			Assert.AreEqual(ReadResp.ContentOneofCase.Event, response.ContentCase);
			actual.Add((Uuid.FromDto(response.Event.Event.Id).ToGuid(), response.Event.Event.StreamRevision));
			await subscription.RequestStream.WriteAsync(new ReadReq
			{
				Ack = new ReadReq.Types.Ack { Ids = { response.Event.Event.Id } }
			});
		}

		CollectionAssert.AreEqual(expected, actual);
	}

	[Test]
	public async Task resolves_a_link_to_an_event_above_int_max_value()
	{
		var linkStream = NewName("links");
		var groupName = NewName("group");
		await Append(
			linkStream,
			new[] { ProposedEvent("$>", $"{IntMaxValue + 1}@{StreamName}") });
		await _persistentSubscriptions.CreateAsync(
			CreateRequest(groupName, 0, linkStream, resolveLinks: true),
			GetCallOptions(AdminCredentials));

		using var subscription = await Subscribe(groupName, linkStream);
		Assert.True(await subscription.ResponseStream.MoveNext());
		var response = subscription.ResponseStream.Current;

		Assert.AreEqual((ulong)(IntMaxValue + 1), response.Event.Event.StreamRevision);
		Assert.AreEqual(_first.EventId, Uuid.FromDto(response.Event.Event.Id).ToGuid());
		Assert.AreEqual(linkStream, response.Event.Link.StreamIdentifier.StreamName.ToStringUtf8());
	}

	private async Task<AsyncDuplexStreamingCall<ReadReq, ReadResp>> Subscribe(
		string groupName,
		string streamName = StreamName)
	{
		var subscription = _persistentSubscriptions.Read(GetCallOptions(AdminCredentials));
		await subscription.RequestStream.WriteAsync(new ReadReq
		{
			Options = new ReadReq.Types.Options
			{
				BufferSize = 1,
				GroupName = groupName,
				StreamIdentifier = StreamIdentifier(streamName),
				UuidOption = new ReadReq.Types.Options.Types.UUIDOption { Structured = new Empty() }
			}
		});
		Assert.True(await subscription.ResponseStream.MoveNext());
		Assert.AreEqual(ReadResp.ContentOneofCase.SubscriptionConfirmation,
			subscription.ResponseStream.Current.ContentCase);
		return subscription;
	}

	private async Task Append(
		string streamName,
		IEnumerable<BatchAppendReq.Types.ProposedMessage> events,
		ulong? expectedRevision = null)
	{
		using var call = _streams.BatchAppend(GetCallOptions(AdminCredentials));
		var options = new BatchAppendReq.Types.Options
		{
			StreamIdentifier = StreamIdentifier(streamName)
		};
		if (expectedRevision.HasValue)
		{
			options.StreamPosition = expectedRevision.Value;
		}
		else
		{
			options.Any = new();
		}

		await call.RequestStream.WriteAsync(new BatchAppendReq
		{
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			Options = options,
			ProposedMessages = { events }
		});
		await call.RequestStream.CompleteAsync();
		Assert.True(await call.ResponseStream.MoveNext());
		Assert.NotNull(call.ResponseStream.Current.Success);
	}

	private static BatchAppendReq.Types.ProposedMessage ProposedEvent(
		string type,
		string data,
		Guid eventId = default) => new()
		{
			Data = ByteString.CopyFromUtf8(data),
			Id = Uuid.FromGuid(eventId == default ? Guid.NewGuid() : eventId).ToDto(),
			Metadata =
		{
			[GrpcMetadata.ContentType] = GrpcMetadata.ContentTypes.ApplicationJson,
			[GrpcMetadata.Type] = type
		}
		};

	private static CreateReq CreateRequest(
		string groupName,
		ulong revision,
		string streamName = StreamName,
		bool resolveLinks = false) => new()
		{
			Options = new CreateReq.Types.Options
			{
				GroupName = groupName,
				Settings = CreateSettings(resolveLinks),
				Stream = new CreateReq.Types.StreamOptions
				{
					Revision = revision,
					StreamIdentifier = StreamIdentifier(streamName)
				}
			}
		};

	private static UpdateReq UpdateRequest(string groupName, ulong revision) => new()
	{
		Options = new UpdateReq.Types.Options
		{
			GroupName = groupName,
			Settings = UpdateSettings(),
			Stream = new UpdateReq.Types.StreamOptions
			{
				Revision = revision,
				StreamIdentifier = StreamIdentifier(StreamName)
			}
		}
	};

	private static CreateReq.Types.Settings CreateSettings(bool resolveLinks) => new()
	{
		CheckpointAfterMs = 100,
		ConsumerStrategy = "Pinned",
		HistoryBufferSize = 20,
		LiveBufferSize = 10,
		MaxCheckpointCount = 10,
		MaxRetryCount = 10,
		MaxSubscriberCount = 1,
		MessageTimeoutMs = 10000,
		MinCheckpointCount = 1,
		ReadBatchSize = 10,
		ResolveLinks = resolveLinks
	};

	private static UpdateReq.Types.Settings UpdateSettings() => new()
	{
		CheckpointAfterMs = 100,
		ConsumerStrategy = "Pinned",
		HistoryBufferSize = 20,
		LiveBufferSize = 10,
		MaxCheckpointCount = 10,
		MaxRetryCount = 10,
		MaxSubscriberCount = 1,
		MessageTimeoutMs = 10000,
		MinCheckpointCount = 1,
		ReadBatchSize = 10
	};

	private static StreamIdentifier StreamIdentifier(string streamName) => new()
	{
		StreamName = ByteString.CopyFromUtf8(streamName)
	};

	private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
