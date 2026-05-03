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

			await _client.CreateAsync(new CreateReq
			{
				Options = new CreateReq.Types.Options
				{
					GroupName = _groupName,
					Stream = new CreateReq.Types.StreamOptions
					{
						Start = new Empty(),
						StreamIdentifier = new StreamIdentifier
						{
							StreamName = ByteString.CopyFromUtf8(_streamName)
						}
					},
					Settings = Settings
				}
			}, GetCallOptions(AdminCredentials));
		}

		protected override async Task When()
		{
			await _client.DeleteAsync(new DeleteReq
			{
				Options = new DeleteReq.Types.Options
				{
					GroupName = _groupName,
					StreamIdentifier = new StreamIdentifier
					{
						StreamName = ByteString.CopyFromUtf8(_streamName)
					}
				}
			}, GetCallOptions(AdminCredentials));
		}

		[Test]
		public void removes_the_subscription()
		{
			var ex = Assert.ThrowsAsync<RpcException>(async () =>
				await _client.GetInfoAsync(new GetInfoReq
				{
					Options = new GetInfoReq.Types.Options
					{
						GroupName = _groupName,
						StreamIdentifier = new StreamIdentifier
						{
							StreamName = ByteString.CopyFromUtf8(_streamName)
						}
					}
				}, GetCallOptions(AdminCredentials)));

			Assert.AreEqual(StatusCode.NotFound, ex.Status.StatusCode);
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
				await _client.DeleteAsync(new DeleteReq
				{
					Options = new DeleteReq.Types.Options
					{
						GroupName = Guid.NewGuid().ToString(),
						StreamIdentifier = new StreamIdentifier
						{
							StreamName = ByteString.CopyFromUtf8(Guid.NewGuid().ToString())
						}
					}
				}, GetCallOptions(AdminCredentials));
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
