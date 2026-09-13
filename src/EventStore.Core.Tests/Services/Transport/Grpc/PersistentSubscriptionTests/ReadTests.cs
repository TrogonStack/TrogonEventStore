using System;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using ReadReq = EventStore.Client.PersistentSubscriptions.ReadReq;
using ReadResp = EventStore.Client.PersistentSubscriptions.ReadResp;

namespace EventStore.Core.Tests.Services.Transport.Grpc.PersistentSubscriptionTests;

[NonParallelizable]
[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class ReadTests<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	private PersistentSubscriptions.PersistentSubscriptionsClient _client;

	protected override Task Given()
	{
		_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);
		return Task.CompletedTask;
	}

	protected override Task When() => Task.CompletedTask;

	[Test]
	public async Task connecting_to_a_missing_subscription_returns_not_found()
	{
		using var subscription = _client.Read(GetCallOptions(AdminCredentials));
		await subscription.RequestStream.WriteAsync(ReadOptions(NewName("stream"), NewName("group")));

		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await subscription.ResponseStream.MoveNext());

		Assert.AreEqual(StatusCode.NotFound, exception.StatusCode);
	}

	[Test]
	public async Task connecting_without_permission_returns_permission_denied()
	{
		var streamName = $"${NewName("stream")}";
		var groupName = NewName("group");
		await CreateSubscription(streamName, groupName);

		using var subscription = _client.Read(GetCallOptions());
		await subscription.RequestStream.WriteAsync(ReadOptions(streamName, groupName));

		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await subscription.ResponseStream.MoveNext());

		Assert.AreEqual(StatusCode.PermissionDenied, exception.StatusCode);
	}

	[Test]
	public async Task connecting_to_a_missing_all_subscription_returns_not_found()
	{
		using var subscription = _client.Read(GetCallOptions(AdminCredentials));
		await subscription.RequestStream.WriteAsync(ReadAllOptions(NewName("group")));

		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await subscription.ResponseStream.MoveNext());

		Assert.AreEqual(StatusCode.NotFound, exception.StatusCode);
	}

	[Test]
	public async Task connecting_to_an_existing_all_subscription_is_confirmed()
	{
		var groupName = NewName("group");
		await CreateAllSubscription(groupName);

		using var subscription = _client.Read(GetCallOptions(AdminCredentials));
		await subscription.RequestStream.WriteAsync(ReadAllOptions(groupName));

		Assert.IsTrue(await subscription.ResponseStream.MoveNext());
		Assert.AreEqual(
			ReadResp.ContentOneofCase.SubscriptionConfirmation,
			subscription.ResponseStream.Current.ContentCase);
	}

	[Test]
	public async Task connecting_to_an_all_subscription_without_permission_returns_permission_denied()
	{
		var groupName = NewName("group");
		await CreateAllSubscription(groupName);

		using var subscription = _client.Read(GetCallOptions());
		await subscription.RequestStream.WriteAsync(ReadAllOptions(groupName));

		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await subscription.ResponseStream.MoveNext());

		Assert.AreEqual(StatusCode.PermissionDenied, exception.StatusCode);
	}

	[Test]
	public async Task connecting_beyond_the_subscriber_limit_returns_failed_precondition()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await CreateSubscription(streamName, groupName, maxSubscriberCount: 1);

		using var first = await Subscribe(streamName, groupName);
		using var second = _client.Read(GetCallOptions(AdminCredentials));
		await second.RequestStream.WriteAsync(ReadOptions(streamName, groupName));

		var exception = Assert.ThrowsAsync<RpcException>(async () =>
			await second.ResponseStream.MoveNext());

		Assert.AreEqual(StatusCode.FailedPrecondition, exception.StatusCode);
	}

	[Test]
	public async Task subscription_starts_at_the_requested_revision_and_acknowledges_the_event()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await Append(streamName, CreateEvents(3).ToArray());
		await CreateSubscription(streamName, groupName, revision: 1);

		using var subscription = await Subscribe(streamName, groupName, bufferSize: 1);
		var response = await ReadEvent(subscription);

		Assert.AreEqual(1, response.Event.Event.StreamRevision);

		await subscription.RequestStream.WriteAsync(new ReadReq
		{
			Ack = new ReadReq.Types.Ack { Ids = { response.Event.Event.Id } }
		});

		var next = await ReadEvent(subscription);
		Assert.AreEqual(2, next.Event.Event.StreamRevision);
	}

	[Test]
	public async Task subscription_from_end_receives_only_new_events()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await Append(streamName, CreateEvent());
		await CreateSubscription(streamName, groupName, startFromEnd: true);

		using var subscription = await Subscribe(streamName, groupName);
		await Append(streamName, CreateEvent());
		var response = await ReadEvent(subscription);

		Assert.AreEqual(1, response.Event.Event.StreamRevision);
	}

	[Test]
	public async Task subscription_on_a_missing_stream_receives_the_first_live_event()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		var proposedEvent = CreateEvent();
		await CreateSubscription(streamName, groupName);

		using var subscription = await Subscribe(streamName, groupName);
		await Append(streamName, proposedEvent);
		var response = await ReadEvent(subscription);

		Assert.AreEqual(0, response.Event.Event.StreamRevision);
		Assert.AreEqual(Uuid.FromDto(proposedEvent.Id), Uuid.FromDto(response.Event.Event.Id));
	}

	[Test]
	public async Task subscription_waits_for_a_requested_future_revision()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await Append(streamName, CreateEvents(3).ToArray());
		await CreateSubscription(streamName, groupName, revision: 3);

		using var subscription = await Subscribe(streamName, groupName);
		await Append(streamName, CreateEvent());
		var response = await ReadEvent(subscription);

		Assert.AreEqual(3, response.Event.Event.StreamRevision);
	}

	[Test]
	public async Task manual_acknowledgement_drains_multiple_buffer_windows()
	{
		const int eventCount = 20;
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await CreateSubscription(streamName, groupName, startFromEnd: true);

		using var subscription = await Subscribe(streamName, groupName, bufferSize: 5);
		await Append(streamName, CreateEvents(eventCount).ToArray());
		for (var revision = 0; revision < eventCount; revision++)
		{
			var response = await ReadEvent(subscription);
			Assert.AreEqual((ulong)revision, response.Event.Event.StreamRevision);
			await subscription.RequestStream.WriteAsync(new ReadReq
			{
				Ack = new ReadReq.Types.Ack { Ids = { response.Event.Event.Id } }
			});
		}
	}

	[Test]
	public async Task retrying_a_nacked_event_preserves_each_retry_count_until_acknowledged()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await Append(streamName, CreateEvent());
		await CreateSubscription(streamName, groupName);

		using var subscription = await Subscribe(streamName, groupName);
		var response = await ReadEvent(subscription);
		var eventId = response.Event.Event.Id;
		for (var retryCount = 1; retryCount <= 5; retryCount++)
		{
			await subscription.RequestStream.WriteAsync(new ReadReq
			{
				Nack = new ReadReq.Types.Nack
				{
					Action = ReadReq.Types.Nack.Types.Action.Retry,
					Ids = { eventId },
					Reason = "retry"
				}
			});
			response = await ReadEvent(subscription);

			Assert.AreEqual(eventId, response.Event.Event.Id);
			Assert.AreEqual(retryCount, response.Event.RetryCount);
		}

		await subscription.RequestStream.WriteAsync(new ReadReq
		{
			Ack = new ReadReq.Types.Ack { Ids = { eventId } }
		});
	}

	[Test]
	public async Task disconnected_event_with_no_retries_is_not_redelivered()
	{
		var streamName = NewName("stream");
		var groupName = NewName("group");
		await CreateSubscription(streamName, groupName, startFromEnd: true, maxRetryCount: 0);
		var firstEvent = CreateEvent();
		var firstSubscription = await Subscribe(streamName, groupName, bufferSize: 1);
		await Append(streamName, firstEvent);
		await ReadEvent(firstSubscription);
		firstSubscription.Dispose();

		await WaitForNoSubscribers(streamName, groupName);

		using var replacement = await Subscribe(streamName, groupName, bufferSize: 1);
		var nextEvent = CreateEvent();
		await Append(streamName, nextEvent);
		var response = await ReadEvent(replacement);

		Assert.AreEqual(Uuid.FromDto(nextEvent.Id), Uuid.FromDto(response.Event.Event.Id));
	}

	[Test]
	public async Task link_resolution_supports_stream_names_containing_at_symbols()
	{
		var targetStream = $"target@{NewName("domain")}";
		var linkStream = NewName("links");
		var groupName = NewName("group");
		var target = CreateEvent("target-event");
		target.Data = ByteString.CopyFromUtf8("data");
		await Append(targetStream, target);
		await Append(linkStream, LinkTo(0, targetStream));
		await CreateSubscription(linkStream, groupName, resolveLinks: true);

		using var subscription = await Subscribe(linkStream, groupName);
		var response = await ReadEvent(subscription);

		Assert.AreEqual(targetStream, response.Event.Event.StreamIdentifier.StreamName.ToStringUtf8());
		Assert.AreEqual("data", response.Event.Event.Data.ToStringUtf8());
		Assert.AreEqual(linkStream, response.Event.Link.StreamIdentifier.StreamName.ToStringUtf8());
	}

	private async Task CreateSubscription(
		string streamName,
		string groupName,
		ulong? revision = null,
		bool startFromEnd = false,
		bool resolveLinks = false,
		int maxSubscriberCount = 40,
		int maxRetryCount = 10)
	{
		var streamOptions = new CreateReq.Types.StreamOptions
		{
			StreamIdentifier = StreamIdentifier(streamName)
		};
		if (revision.HasValue)
		{
			streamOptions.Revision = revision.Value;
		}
		else if (startFromEnd)
		{
			streamOptions.End = new Empty();
		}
		else
		{
			streamOptions.Start = new Empty();
		}

		await _client.CreateAsync(new CreateReq
		{
			Options = new CreateReq.Types.Options
			{
				GroupName = groupName,
				Stream = streamOptions,
				Settings = new CreateReq.Types.Settings
				{
					CheckpointAfterMs = 100,
					ConsumerStrategy = "Pinned",
					HistoryBufferSize = 20,
					LiveBufferSize = 10,
					MaxCheckpointCount = 10,
					MaxRetryCount = maxRetryCount,
					MaxSubscriberCount = maxSubscriberCount,
					MessageTimeoutMs = 10000,
					MinCheckpointCount = 1,
					ReadBatchSize = 10,
					ResolveLinks = resolveLinks
				}
			}
		}, GetCallOptions(AdminCredentials));
	}

	private async Task CreateAllSubscription(string groupName)
	{
		await _client.CreateAsync(new CreateReq
		{
			Options = new CreateReq.Types.Options
			{
				GroupName = groupName,
				All = new CreateReq.Types.AllOptions
				{
					Start = new Empty(),
					NoFilter = new Empty()
				},
				Settings = new CreateReq.Types.Settings
				{
					CheckpointAfterMs = 100,
					ConsumerStrategy = "Pinned",
					HistoryBufferSize = 20,
					LiveBufferSize = 10,
					MaxCheckpointCount = 10,
					MaxRetryCount = 10,
					MaxSubscriberCount = 40,
					MessageTimeoutMs = 10000,
					MinCheckpointCount = 1,
					ReadBatchSize = 10
				}
			}
		}, GetCallOptions(AdminCredentials));
	}

	private async Task<AsyncDuplexStreamingCall<ReadReq, ReadResp>> Subscribe(
		string streamName,
		string groupName,
		int bufferSize = 10)
	{
		var subscription = _client.Read(GetCallOptions(AdminCredentials));
		await subscription.RequestStream.WriteAsync(ReadOptions(streamName, groupName, bufferSize));
		if (!await subscription.ResponseStream.MoveNext() ||
			subscription.ResponseStream.Current.ContentCase != ReadResp.ContentOneofCase.SubscriptionConfirmation)
		{
			subscription.Dispose();
			throw new InvalidOperationException("Persistent subscription was not confirmed.");
		}

		return subscription;
	}

	private static async Task<ReadResp> ReadEvent(AsyncDuplexStreamingCall<ReadReq, ReadResp> subscription)
	{
		if (!await subscription.ResponseStream.MoveNext() ||
			subscription.ResponseStream.Current.ContentCase != ReadResp.ContentOneofCase.Event)
		{
			throw new InvalidOperationException("Persistent subscription did not return an event.");
		}

		return subscription.ResponseStream.Current;
	}

	private async Task WaitForNoSubscribers(string streamName, string groupName)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		do
		{
			var response = await _client.GetInfoAsync(new GetInfoReq
			{
				Options = new GetInfoReq.Types.Options
				{
					GroupName = groupName,
					StreamIdentifier = StreamIdentifier(streamName)
				}
			}, GetCallOptions(AdminCredentials));
			if (response.SubscriptionInfo.Connections.Count == 0)
			{
				return;
			}

			await Task.Delay(10);
		} while (DateTime.UtcNow < deadline);

		Assert.Fail("Disconnected subscriber remained registered.");
	}

	private async Task Append(string streamName, params BatchAppendReq.Types.ProposedMessage[] events)
	{
		var response = await AppendToStreamBatch(new BatchAppendReq
		{
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			Options = new BatchAppendReq.Types.Options
			{
				Any = new(),
				StreamIdentifier = StreamIdentifier(streamName)
			},
			ProposedMessages = { events }
		});

		Assert.NotNull(response.Success);
	}

	private static BatchAppendReq.Types.ProposedMessage LinkTo(ulong revision, string streamName)
	{
		var link = CreateEvent("$>");
		link.Data = ByteString.CopyFromUtf8($"{revision}@{streamName}");
		return link;
	}

	private static ReadReq ReadOptions(string streamName, string groupName, int bufferSize = 10) => new()
	{
		Options = new ReadReq.Types.Options
		{
			BufferSize = bufferSize,
			GroupName = groupName,
			StreamIdentifier = StreamIdentifier(streamName),
			UuidOption = new ReadReq.Types.Options.Types.UUIDOption { Structured = new Empty() }
		}
	};

	private static ReadReq ReadAllOptions(string groupName, int bufferSize = 10) => new()
	{
		Options = new ReadReq.Types.Options
		{
			All = new Empty(),
			BufferSize = bufferSize,
			GroupName = groupName,
			UuidOption = new ReadReq.Types.Options.Types.UUIDOption { Structured = new Empty() }
		}
	};

	private static StreamIdentifier StreamIdentifier(string streamName) => new()
	{
		StreamName = ByteString.CopyFromUtf8(streamName)
	};

	private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
