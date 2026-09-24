using System;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using Google.Protobuf;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class SubscriptionDisconnectTests<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	protected override Task Given() => Task.CompletedTask;
	protected override Task When() => Task.CompletedTask;

	[TestCase("stream")]
	[TestCase("all")]
	[TestCase("filtered-all")]
	public async Task disposing_the_grpc_read_call_unsubscribes_its_live_subscription(string kind)
	{
		var unsubscribed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
		var handler = new AdHocHandler<ClientMessage.UnsubscribeFromStream>(message =>
			unsubscribed.TrySetResult(message.CorrelationId));
		Node.Node.MainBus.Subscribe(handler);
		try
		{
			using var call = StreamsClient.Read(CreateReadRequest(kind), GetCallOptions(AdminCredentials));
			Assert.That(await call.ResponseStream.MoveNext(CancellationToken.None), Is.True);
			var confirmation = call.ResponseStream.Current;
			Assert.That(confirmation.ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.Confirmation));
			var subscriptionId = Guid.Parse(confirmation.Confirmation.SubscriptionId);

			call.Dispose();

			Assert.That(await unsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(subscriptionId));
		}
		finally
		{
			Node.Node.MainBus.Unsubscribe(handler);
		}
	}

	private static ReadReq CreateReadRequest(string kind)
	{
		var options = new ReadReq.Types.Options
		{
			Subscription = new(),
			UuidOption = new() { Structured = new() },
			ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
		};

		switch (kind)
		{
			case "stream":
				options.Stream = new()
				{
					End = new(),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8("subscription-disconnect") }
				};
				options.NoFilter = new();
				break;
			case "all":
				options.All = new() { End = new() };
				options.NoFilter = new();
				break;
			case "filtered-all":
				options.All = new() { End = new() };
				options.Filter = new()
				{
					Max = 32,
					CheckpointIntervalMultiplier = 1,
					StreamIdentifier = new() { Prefix = { "subscription-disconnect" } }
				};
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(kind));
		}

		return new ReadReq { Options = options };
	}
}
