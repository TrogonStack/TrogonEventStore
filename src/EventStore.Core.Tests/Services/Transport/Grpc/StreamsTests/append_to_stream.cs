using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using EventStore.Core.Services.Transport.Grpc;
using Google.Protobuf;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class append_to_stream<TLogFormat, TStreamId> : GrpcSpecification<TLogFormat, TStreamId>
{
	protected override Task Given() => Task.CompletedTask;

	protected override Task When() => Task.CompletedTask;

	[Test]
	public async Task can_append_to_stream_with_long_name()
	{
		var streamName = nameof(can_append_to_stream_with_long_name) + new string('A', 300);
		var response = await Append(streamName, CreateEvents(1));

		Assert.That(response.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
		Assert.That(response.Success.CurrentRevision, Is.Zero);
	}

	[Test]
	public async Task can_append_multiple_events_to_stream_with_long_name_at_once()
	{
		var streamName = nameof(can_append_multiple_events_to_stream_with_long_name_at_once) + new string('A', 300);
		var response = await Append(streamName, CreateEvents(3));

		Assert.That(response.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
		Assert.That(response.Success.CurrentRevision, Is.EqualTo(2));
	}

	[Test]
	public async Task can_append_event_with_long_event_type()
	{
		var response = await Append(nameof(can_append_event_with_long_event_type),
			new[] { CreateEvent(new string('A', 300)) });

		Assert.That(response.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
		Assert.That(response.Success.CurrentRevision, Is.Zero);
	}

	[Test]
	public async Task can_append_multiple_events_with_long_event_type_at_once()
	{
		var eventType = new string('A', 300);
		var response = await Append(nameof(can_append_multiple_events_with_long_event_type_at_once),
			Enumerable.Range(0, 3).Select(_ => CreateEvent(eventType)));

		Assert.That(response.ResultCase, Is.EqualTo(BatchAppendResp.ResultOneofCase.Success));
		Assert.That(response.Success.CurrentRevision, Is.EqualTo(2));
	}

	private ValueTask<BatchAppendResp> Append(
		string streamName,
		System.Collections.Generic.IEnumerable<BatchAppendReq.Types.ProposedMessage> events) =>
		AppendToStreamBatch(new BatchAppendReq
		{
			Options = new()
			{
				NoStream = new(),
				StreamIdentifier = new() { StreamName = ByteString.CopyFromUtf8(streamName) }
			},
			CorrelationId = Uuid.NewUuid().ToDto(),
			IsFinal = true,
			ProposedMessages = { events }
		});
}
