using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using Streams = EventStore.Client.Streams.Streams;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
[Category("LongRunning")]
public class StreamRevisionAboveIntMaxTests<TLogFormat, TStreamId>
	: GrpcSpecificationWithExistingRecords<TLogFormat, TStreamId>
{
	private const long FirstRevision = (long)int.MaxValue + 1;
	private const string StreamName = "grpc-stream-revision-above-int-max";
	private GrpcStreamEdgeOperations _grpc;
	private readonly Guid[] _eventIds = new Guid[5];

	public override async ValueTask WriteTestScenario(CancellationToken token)
	{
		for (var index = 0; index < _eventIds.Length; index++)
		{
			var record = await WriteSingleEvent(StreamName, FirstRevision + index,
				new string('.', 3000), token: token);
			_eventIds[index] = record.EventId;
		}
	}

	public override async Task Given()
	{
		_grpc = new GrpcStreamEdgeOperations(Channel);
		var metadata = await _grpc.Append("$$" + StreamName, data: "{\"$tb\":2147483648}",
			eventType: "$metadata");
		Assert.That(metadata.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
	}

	[Test]
	public async Task reads_revisions_above_int_max_in_both_directions()
	{
		var forwards = await _grpc.Read(StreamName, (ulong)FirstRevision, 5);
		var backwards = await _grpc.Read(StreamName, (ulong)(FirstRevision + 4), 5,
			ReadReq.Types.Options.Types.ReadDirection.Backwards);

		CollectionAssert.AreEqual(_eventIds,
			forwards.Where(x => x.Event is not null)
				.Select(x => EventStore.Core.Services.Transport.Grpc.Uuid.FromDto(x.Event.Event.Id).ToGuid()));
		CollectionAssert.AreEqual(_eventIds.Reverse(),
			backwards.Where(x => x.Event is not null)
				.Select(x => EventStore.Core.Services.Transport.Grpc.Uuid.FromDto(x.Event.Event.Id).ToGuid()));
		CollectionAssert.AreEqual(Enumerable.Range(0, 5).Select(x => (ulong)(FirstRevision + x)),
			forwards.Where(x => x.Event is not null).Select(x => x.Event.Event.StreamRevision));
	}

	[Test]
	public async Task appends_at_a_revision_above_int_max_and_rejects_an_incorrect_revision()
	{
		var success = await _grpc.Append(StreamName, expectedRevision: (ulong)(FirstRevision + 4));
		Assert.That(success.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
		Assert.That(success.Success.CurrentRevision, Is.EqualTo((ulong)(FirstRevision + 5)));

		var mismatch = await _grpc.Append(StreamName, expectedRevision: (ulong)(FirstRevision + 15));
		Assert.That(mismatch.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Error));
		Assert.That(mismatch.Error.Code, Is.EqualTo(Google.Rpc.Code.AlreadyExists));
	}

	[Test]
	public async Task catch_up_subscription_delivers_revisions_above_int_max()
	{
		var client = new Streams.StreamsClient(Channel);
		using var subscription = client.Read(new ReadReq
		{
			Options = new()
			{
				Subscription = new(),
				NoFilter = new(),
				ReadDirection = ReadReq.Types.Options.Types.ReadDirection.Forwards,
				UuidOption = new() { Structured = new() },
				Stream = new()
				{
					Revision = (ulong)(FirstRevision - 1),
					StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(StreamName) }
				}
			}
		}, GetCallOptions(AdminCredentials).WithDeadline(DateTime.UtcNow.AddSeconds(20)));

		Assert.That(await subscription.ResponseStream.MoveNext(), Is.True);
		Assert.That(subscription.ResponseStream.Current.ContentCase,
			Is.EqualTo(ReadResp.ContentOneofCase.Confirmation));

		for (var index = 0; index < _eventIds.Length; index++)
		{
			Assert.That(await subscription.ResponseStream.MoveNext(), Is.True);
			var response = subscription.ResponseStream.Current;
			Assert.That(response.ContentCase, Is.EqualTo(ReadResp.ContentOneofCase.Event));
			Assert.That(response.Event.Event.StreamRevision,
				Is.EqualTo((ulong)(FirstRevision + index)));
			Assert.That(EventStore.Core.Services.Transport.Grpc.Uuid.FromDto(response.Event.Event.Id).ToGuid(),
				Is.EqualTo(_eventIds[index]));
		}
	}
}
