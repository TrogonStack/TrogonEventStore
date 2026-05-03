using System;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.PersistentSubscriptionTests;

public class UpdateTests
{
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_updating_an_existing_persistent_subscription_on_stream<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId>
	{
		private PersistentSubscriptions.PersistentSubscriptionsClient _client;
		private readonly string _streamName = Guid.NewGuid().ToString();
		private readonly string _groupName = Guid.NewGuid().ToString();
		private SubscriptionInfo _info;

		protected override async Task Given()
		{
			_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

			await _client.CreateAsync(CreateRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
		}

		protected override async Task When()
		{
			await _client.UpdateAsync(UpdateRequest(_streamName, _groupName, "PinnedByCorrelation"),
				GetCallOptions(AdminCredentials));

			_info = (await _client.GetInfoAsync(GetInfoRequest(_streamName, _groupName),
				GetCallOptions(AdminCredentials))).SubscriptionInfo;
		}

		[Test]
		public void applies_the_updated_settings()
		{
			Assert.AreEqual("PinnedByCorrelation", _info.NamedConsumerStrategy);
			Assert.AreEqual(30000, _info.MessageTimeoutMilliseconds);
			Assert.IsTrue(_info.ExtraStatistics);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_updating_with_the_legacy_consumer_strategy_field<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId>
	{
		private PersistentSubscriptions.PersistentSubscriptionsClient _client;
		private readonly string _streamName = Guid.NewGuid().ToString();
		private readonly string _groupName = Guid.NewGuid().ToString();
		private SubscriptionInfo _info;

		protected override async Task Given()
		{
			_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

			await _client.CreateAsync(CreateRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
		}

		protected override async Task When()
		{
			var request = UpdateRequest(_streamName, _groupName);
			#pragma warning disable 612
			request.Options.Settings.NamedConsumerStrategy = UpdateReq.Types.ConsumerStrategy.Pinned;
			#pragma warning restore 612

			await _client.UpdateAsync(request, GetCallOptions(AdminCredentials));

			_info = (await _client.GetInfoAsync(GetInfoRequest(_streamName, _groupName),
				GetCallOptions(AdminCredentials))).SubscriptionInfo;
		}

		[Test]
		public void preserves_the_legacy_enum_path()
		{
			Assert.AreEqual("Pinned", _info.NamedConsumerStrategy);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_updating_without_permissions<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId>
	{
		private PersistentSubscriptions.PersistentSubscriptionsClient _client;
		private readonly string _streamName = Guid.NewGuid().ToString();
		private readonly string _groupName = Guid.NewGuid().ToString();
		private Exception _exception;

		protected override async Task Given()
		{
			_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

			await _client.CreateAsync(CreateRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
		}

		protected override async Task When()
		{
			try
			{
				await _client.UpdateAsync(UpdateRequest(_streamName, _groupName), GetCallOptions());
			}
			catch (Exception ex)
			{
				_exception = ex;
			}
		}

		[Test]
		public void returns_permission_denied()
		{
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.PermissionDenied, ((RpcException)_exception).Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_updating_a_missing_persistent_subscription_on_stream<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId>
	{
		private PersistentSubscriptions.PersistentSubscriptionsClient _client;
		private Exception _exception;

		protected override Task Given()
		{
			_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);
			return Task.CompletedTask;
		}

		protected override async Task When()
		{
			try
			{
				await _client.UpdateAsync(
					UpdateRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()),
					GetCallOptions(AdminCredentials));
			}
			catch (Exception ex)
			{
				_exception = ex;
			}
		}

		[Test]
		public void returns_not_found()
		{
			Assert.IsInstanceOf<RpcException>(_exception);
			Assert.AreEqual(StatusCode.NotFound, ((RpcException)_exception).Status.StatusCode);
		}
	}

	private static CreateReq CreateRequest(string streamName, string groupName) => new()
	{
		Options = new CreateReq.Types.Options
		{
			GroupName = groupName,
			Stream = new CreateReq.Types.StreamOptions
			{
				Start = new Empty(),
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(streamName)
				}
			},
			Settings = CreateSettings
		}
	};

	private static UpdateReq UpdateRequest(string streamName, string groupName, string consumerStrategy = "") => new()
	{
		Options = new UpdateReq.Types.Options
		{
			GroupName = groupName,
			Stream = new UpdateReq.Types.StreamOptions
			{
				Start = new Empty(),
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(streamName)
				}
			},
			Settings = new UpdateReq.Types.Settings
			{
				CheckpointAfterMs = 10000,
				ExtraStatistics = true,
				MaxCheckpointCount = 20,
				MinCheckpointCount = 10,
				MaxRetryCount = 30,
				MaxSubscriberCount = 40,
				MessageTimeoutMs = 30000,
				HistoryBufferSize = 60,
				LiveBufferSize = 10,
				ConsumerStrategy = consumerStrategy,
				ReadBatchSize = 50
			}
		}
	};

	private static GetInfoReq GetInfoRequest(string streamName, string groupName) => new()
	{
		Options = new GetInfoReq.Types.Options
		{
			GroupName = groupName,
			StreamIdentifier = new StreamIdentifier
			{
				StreamName = ByteString.CopyFromUtf8(streamName)
			}
		}
	};

	private static CreateReq.Types.Settings CreateSettings => new()
	{
		CheckpointAfterMs = 10000,
		MaxCheckpointCount = 20,
		MinCheckpointCount = 10,
		MaxRetryCount = 30,
		MaxSubscriberCount = 40,
		MessageTimeoutMs = 20000,
		HistoryBufferSize = 60,
		LiveBufferSize = 10,
		ConsumerStrategy = "RoundRobin",
		ReadBatchSize = 50
	};
}
