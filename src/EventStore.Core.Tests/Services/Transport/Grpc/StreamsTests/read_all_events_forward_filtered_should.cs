using System.Linq;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture(typeof(LogFormat.V2), typeof(string))]
public class read_all_events_forward_filtered_should<TLogFormat, TStreamId>
	: FilteredReadAllSpecification<TLogFormat, TStreamId>
{
	private const ReadReq.Types.Options.Types.ReadDirection Direction =
		ReadReq.Types.Options.Types.ReadDirection.Forwards;

	[Test]
	public async Task only_return_events_with_a_given_stream_prefix()
	{
		var events = await Read(Direction, StreamPrefix(StreamA));

		Assert.That(events, Has.Length.EqualTo(10));
		Assert.That(events.All(x => StreamName(x) == StreamA), Is.True);
		Assert.That(events.Select(x => x.Event.StreamRevision), Is.EqualTo(Enumerable.Range(0, 10)));
	}

	[Test]
	public async Task only_return_events_with_a_given_event_prefix()
	{
		var events = await Read(Direction, EventTypePrefix("AE"));

		Assert.That(events, Has.Length.EqualTo(10));
		Assert.That(events.All(x => EventType(x) == "AEvent"), Is.True);
	}

	[Test]
	public async Task only_return_events_that_satisfy_a_given_stream_regex()
	{
		var events = await Read(Direction, StreamRegex("^.*eam-b.*$"));

		Assert.That(events, Has.Length.EqualTo(10));
		Assert.That(events.All(x => StreamName(x) == StreamB), Is.True);
		Assert.That(events.Select(x => x.Event.StreamRevision), Is.EqualTo(Enumerable.Range(0, 10)));
	}

	[Test]
	public async Task only_return_events_that_satisfy_a_given_event_regex()
	{
		var events = await Read(Direction, EventTypeRegex("^.*BEv.*$"));

		Assert.That(events, Has.Length.EqualTo(10));
		Assert.That(events.All(x => EventType(x) == "BEvent"), Is.True);
	}

	[Test]
	public async Task only_return_events_that_are_not_system_events()
	{
		var events = await Read(Direction, StreamRegex("^[^$].*"));

		Assert.That(events, Is.Not.Empty);
		Assert.That(events.All(x => !EventType(x).StartsWith("$")), Is.True);
	}
}
