using System;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using NUnit.Framework;

namespace EventStore.Core.Tests.ClientOperations;

[TestFixture]
public class when_creating_transaction_start_message
{
	[Test]
	public void accepts_soft_deleted_expected_version()
	{
		Assert.DoesNotThrow(() => new ClientMessage.TransactionStart(
			Guid.NewGuid(),
			Guid.NewGuid(),
			new NoopEnvelope(),
			requireLeader: true,
			"test-stream",
			ExpectedVersion.SoftDeleted,
			user: null));
	}

	[Test]
	public void rejects_stream_exists_expected_version()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new ClientMessage.TransactionStart(
			Guid.NewGuid(),
			Guid.NewGuid(),
			new NoopEnvelope(),
			requireLeader: true,
			"test-stream",
			ExpectedVersion.StreamExists,
			user: null));
	}
}
