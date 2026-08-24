using System;
using System.Reflection;
using EventStore.Core.Services.Transport.Enumerators;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Grpc.StreamsTests;

[TestFixture]
public class SubscriptionStatusMappingTests
{
	private static readonly DateTime TransitionTimestamp = new(2026, 8, 24, 12, 34, 56, DateTimeKind.Utc);

	[Test]
	public void caught_up_preserves_the_transition_timestamp()
	{
		var response = Map(new ReadResponse.SubscriptionCaughtUp(42, TransitionTimestamp));
		Assert.That(response.CaughtUp.Timestamp.ToDateTime(), Is.EqualTo(TransitionTimestamp));
	}

	[Test]
	public void fell_behind_preserves_the_transition_timestamp()
	{
		var response = Map(new ReadResponse.SubscriptionFellBehind(42, TransitionTimestamp));
		Assert.That(response.FellBehind.Timestamp.ToDateTime(), Is.EqualTo(TransitionTimestamp));
	}

	private static EventStore.Client.Streams.ReadResp Map(ReadResponse response)
	{
		var streamsType = typeof(ReadResponse).Assembly
			.GetType("EventStore.Core.Services.Transport.Grpc.Streams`1")
			.MakeGenericType(typeof(string));
		var method = streamsType.GetMethod("TryConvertReadResponse", BindingFlags.NonPublic | BindingFlags.Static);
		var arguments = new object[] { response, null, null };

		Assert.That(method, Is.Not.Null);
		Assert.That(method.Invoke(null, arguments), Is.True);
		return (EventStore.Client.Streams.ReadResp)arguments[2];
	}
}
