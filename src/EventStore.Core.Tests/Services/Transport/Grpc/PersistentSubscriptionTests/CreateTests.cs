using System;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.PersistentSubscriptionTests;

[TestFixture(typeof(LogFormat.V2), typeof(string), false)]
[TestFixture(typeof(LogFormat.V2), typeof(string), true)]
public class CreateTests<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	private readonly bool _legacy;

	public CreateTests(bool legacy)
	{
		_legacy = legacy;
	}
	protected override Task Given() => Task.CompletedTask;

	protected override Task When() => Task.CompletedTask;

	[Test]
	public async Task can_create_persistent_subscription()
	{
		var client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

		await client.CreateAsync(CreateRequest(), GetCallOptions(AdminCredentials));
	}

	[Test]
	public async Task creating_duplicate_persistent_subscription_returns_already_exists()
	{
		var client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);
		var streamName = NewName("stream");
		var groupName = NewName("group");
		var request = CreateRequest(streamName, groupName);

		await client.CreateAsync(request, GetCallOptions(AdminCredentials));

		var ex = Assert.ThrowsAsync<RpcException>(async () =>
			await client.CreateAsync(request, GetCallOptions(AdminCredentials)));

		Assert.AreEqual(StatusCode.AlreadyExists, ex.Status.StatusCode);
	}

	[Test]
	public void creating_persistent_subscription_without_permissions_returns_permission_denied()
	{
		var client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

		var ex = Assert.ThrowsAsync<RpcException>(async () =>
			await client.CreateAsync(CreateRequest(), GetCallOptions()));

		Assert.AreEqual(StatusCode.PermissionDenied, ex.Status.StatusCode);
	}

	[Test]
	public void creating_persistent_subscription_with_bad_config_returns_invalid_argument()
	{
		var client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);
		var settings = Settings();
		settings.HistoryBufferSize = 10;
		settings.ReadBatchSize = 11;

		var ex = Assert.ThrowsAsync<RpcException>(async () =>
			await client.CreateAsync(CreateRequest(settings: settings), GetCallOptions(AdminCredentials)));

		Assert.AreEqual(StatusCode.InvalidArgument, ex.Status.StatusCode);
	}

	[Test]
	public async Task can_create_persistent_subscription_with_message_timeout_zero()
	{
		var client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);
		var settings = Settings(messageTimeoutMs: 0);

		await client.CreateAsync(CreateRequest(settings: settings), GetCallOptions(AdminCredentials));
	}

	[Test]
	public async Task can_create_persistent_subscription_without_message_timeout()
	{
		var client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

		await client.CreateAsync(
			CreateRequest(settings: Settings(includeMessageTimeout: false)),
			GetCallOptions(AdminCredentials));
	}

	private CreateReq CreateRequest(
		string streamName = null,
		string groupName = null,
		CreateReq.Types.Settings settings = null) => new()
	{
		Options = new CreateReq.Types.Options
		{
			GroupName = groupName ?? NewName("group"),
			Stream = new CreateReq.Types.StreamOptions
			{
				Start = new Empty(),
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(streamName ?? NewName("stream"))
				}
			},
			Settings = settings ?? Settings()
		}
	};

	private CreateReq.Types.Settings Settings(int messageTimeoutMs = 20000, bool includeMessageTimeout = true)
	{
		var settings = new CreateReq.Types.Settings
		{
			CheckpointAfterMs = 10000,
			ExtraStatistics = true,
			MaxCheckpointCount = 20,
			MinCheckpointCount = 10,
			MaxRetryCount = 30,
			MaxSubscriberCount = 40,
			HistoryBufferSize = 60,
			LiveBufferSize = 10,
			ReadBatchSize = 50
		};

		if (includeMessageTimeout)
			settings.MessageTimeoutMs = messageTimeoutMs;

		if (_legacy)
		{
#pragma warning disable 612
			settings.NamedConsumerStrategy = CreateReq.Types.ConsumerStrategy.Pinned;
#pragma warning restore 612
		}
		else
		{
			settings.ConsumerStrategy = "Pinned";
		}

		return settings;
	}

	private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
