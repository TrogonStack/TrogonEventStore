using System;
using System.Threading.Tasks;
using EventStore.Client;
using EventStore.Client.PersistentSubscriptions;
using EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.PersistentSubscriptionTests;

public class DeleteTests
{
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_deleting_an_existing_persistent_subscription_on_stream<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId>
	{
		private PersistentSubscriptions.PersistentSubscriptionsClient _client;
		private readonly string _streamName = Guid.NewGuid().ToString();
		private readonly string _groupName = Guid.NewGuid().ToString();

		protected override async Task Given()
		{
			_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

			await _client.CreateAsync(CreateRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
		}

		protected override async Task When()
		{
			await _client.DeleteAsync(DeleteRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
		}

		[Test]
		public void removes_the_subscription()
		{
			var ex = Assert.ThrowsAsync<RpcException>(async () =>
				await _client.GetInfoAsync(GetInfoRequest(_streamName, _groupName), GetCallOptions(AdminCredentials)));

			Assert.AreEqual(StatusCode.NotFound, ex.Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_deleting_an_existing_persistent_subscription_without_permissions<TLogFormat, TStreamId>
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
				await _client.DeleteAsync(DeleteRequest(_streamName, _groupName), GetCallOptions());
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

		[Test]
		public async Task leaves_the_subscription_available()
		{
			var response = await _client.GetInfoAsync(GetInfoRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));

			Assert.AreEqual(_groupName, response.SubscriptionInfo.GroupName);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_deleting_an_existing_persistent_subscription_with_subscribers<TLogFormat, TStreamId>
		: GrpcSpecification<TLogFormat, TStreamId>
	{
		private PersistentSubscriptions.PersistentSubscriptionsClient _client;
		private AsyncDuplexStreamingCall<ReadReq, ReadResp> _subscription;
		private readonly string _streamName = Guid.NewGuid().ToString();
		private readonly string _groupName = Guid.NewGuid().ToString();

		protected override async Task Given()
		{
			_client = new PersistentSubscriptions.PersistentSubscriptionsClient(Channel);

			await _client.CreateAsync(CreateRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
			_subscription = await SubscribeToPersistentSubscription(
				_client, _streamName, _groupName, GetCallOptions(AdminCredentials));
		}

		protected override async Task When()
		{
			await _client.DeleteAsync(DeleteRequest(_streamName, _groupName), GetCallOptions(AdminCredentials));
		}

		[OneTimeTearDown]
		public void DisposeSubscription()
		{
			_subscription?.Dispose();
		}

		[Test]
		public void removes_the_subscription()
		{
			var ex = Assert.ThrowsAsync<RpcException>(async () =>
				await _client.GetInfoAsync(GetInfoRequest(_streamName, _groupName), GetCallOptions(AdminCredentials)));

			Assert.AreEqual(StatusCode.NotFound, ex.Status.StatusCode);
		}

		[Test]
		public void drops_the_active_subscription()
		{
			var ex = Assert.ThrowsAsync<RpcException>(async () =>
				await _subscription.ResponseStream.MoveNext());

			Assert.AreEqual(StatusCode.Cancelled, ex.Status.StatusCode);
		}
	}

	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_deleting_a_missing_persistent_subscription_on_stream<TLogFormat, TStreamId>
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
				await _client.DeleteAsync(
					DeleteRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString()),
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

	private static async Task<AsyncDuplexStreamingCall<ReadReq, ReadResp>> SubscribeToPersistentSubscription(
		PersistentSubscriptions.PersistentSubscriptionsClient client, string streamName, string groupName, CallOptions callOptions)
	{
		var call = client.Read(callOptions);

		await call.RequestStream.WriteAsync(new ReadReq
		{
			Options = new ReadReq.Types.Options
			{
				GroupName = groupName,
				StreamIdentifier = new StreamIdentifier
				{
					StreamName = ByteString.CopyFromUtf8(streamName)
				},
				UuidOption = new ReadReq.Types.Options.Types.UUIDOption
				{
					Structured = new Empty()
				},
				BufferSize = 10
			}
		});

		if (!await call.ResponseStream.MoveNext() ||
			call.ResponseStream.Current.ContentCase != ReadResp.ContentOneofCase.SubscriptionConfirmation)
		{
			throw new InvalidOperationException();
		}

		return call;
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
			Settings = Settings
		}
	};

	private static DeleteReq DeleteRequest(string streamName, string groupName) => new()
	{
		Options = new DeleteReq.Types.Options
		{
			GroupName = groupName,
			StreamIdentifier = new StreamIdentifier
			{
				StreamName = ByteString.CopyFromUtf8(streamName)
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

	private static CreateReq.Types.Settings Settings => new()
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
