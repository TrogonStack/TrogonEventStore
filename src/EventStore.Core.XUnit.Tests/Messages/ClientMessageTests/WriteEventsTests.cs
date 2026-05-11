using System;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Messages.ClientMessageTests;

public class WriteEventsTests
{
	private static ClientMessage.WriteEvents CreateSut(
		string eventStreamId = "normal",
		long expectedVersion = default) =>
		new(
			internalCorrId: Guid.NewGuid(),
			correlationId: Guid.NewGuid(),
			envelope: new NoopEnvelope(),
			requireLeader: false,
			eventStreamId: eventStreamId,
			expectedVersion: expectedVersion,
			events: [],
			user: default);

	[Theory]
	[InlineData("normal")]
	[InlineData("  not normal  ")]
	[InlineData("$$normal")]
	public void accepts_valid_stream_ids(string streamId)
	{
		CreateSut(eventStreamId: streamId);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("$$")]
	public void rejects_invalid_stream_ids(string streamId)
	{
		var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
		{
			CreateSut(eventStreamId: streamId);
		});

		Assert.Equal("eventStreamId", ex.ParamName);
	}
}
