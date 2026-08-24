using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture]
public class SubscribeToStreamTests
{
	[TestFixture(typeof(LogFormat.V2), typeof(string))]
	public class when_subscribing_to_stream<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
	{
		private const string StreamId = nameof(when_subscribing_to_stream<TLogFormat, TStreamId>);
		private readonly List<ReadResp> _responses = new();
		private ulong _positionOfLastWrite;
		private DateTime _subscriptionStartedAt;

		public when_subscribing_to_stream() : base(new LotsOfExpiriesStrategy())
		{
		}

		protected override async Task Given()
		{
			var response = await AppendToStreamBatch(new BatchAppendReq
			{
				Options = new()
				{
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamId) },
					Any = new(),
				},
				CorrelationId = Uuid.NewUuid().ToDto(),
				IsFinal = true,
				ProposedMessages = { CreateEvents(120) }
			});
			_positionOfLastWrite = response.Success.CurrentRevision;
		}

		protected override async Task When()
		{
			_subscriptionStartedAt = DateTime.UtcNow;
			using var call = StreamsClient.Read(new()
			{
				Options = new()
				{
					Subscription = new(),
					NoFilter = new(),
					ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
					UuidOption = new() { Structured = new() },
					Stream = new()
					{
						Start = new(),
						StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamId) },
					},
				}
			}, GetCallOptions(AdminCredentials));

			while (await call.ResponseStream.MoveNext())
			{
				var response = call.ResponseStream.Current;
				_responses.Add(response);
				if (response.ContentCase == ReadResp.ContentOneofCase.CaughtUp)
					break;
			}

			// caught up, now add one more event

			await AppendToStreamBatch(new BatchAppendReq
			{
				Options = new()
				{
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamId) },
					Any = new(),
				},
				CorrelationId = Uuid.NewUuid().ToDto(),
				IsFinal = true,
				ProposedMessages = { CreateEvents(1) }
			});

			// and consume it. this makes sure we didnt fail while transitioning to live
			Assert.True(await call.ResponseStream.MoveNext());
		}

		[Test]
		public void subscription_confirmed()
		{
			Assert.AreEqual(ReadResp.ContentOneofCase.Confirmation, _responses[0].ContentCase);
			Assert.NotNull(_responses[0].Confirmation.SubscriptionId);
		}

		[Test]
		public void caught_up_includes_the_stream_revision()
		{
			var caughtUp = _responses.Single(x => x.ContentCase == ReadResp.ContentOneofCase.CaughtUp).CaughtUp;
			Assert.True(caughtUp.HasStreamRevision);
			Assert.AreEqual((long)_positionOfLastWrite, caughtUp.StreamRevision);
		}

		[Test]
		public void caught_up_includes_the_server_timestamp()
		{
			var caughtUp = _responses.Single(x => x.ContentCase == ReadResp.ContentOneofCase.CaughtUp).CaughtUp;
			Assert.That(caughtUp.Timestamp.ToDateTime(), Is.InRange(_subscriptionStartedAt, DateTime.UtcNow));
		}
	}
}
