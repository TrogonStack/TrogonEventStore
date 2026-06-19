using System.Collections.Generic;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.RequestManager.Managers;
using EventStore.Core.Tests.Fakes;
using EventStore.Core.Tests.Helpers;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.RequestManagement.WriteStreamMgr;

[TestFixture]
public class when_write_stream_gets_stream_deleted : RequestManagerSpecification<WriteEvents>
{
	private readonly long _expectedVersion = ExpectedVersion.StreamExists;

	protected override WriteEvents OnManager(FakePublisher publisher)
	{
		return new WriteEvents(
			publisher,
			CommitTimeout,
			Envelope,
			InternalCorrId,
			ClientCorrId,
			"test123",
			_expectedVersion,
			new[] { DummyEvent() },
			CommitSource);
	}

	protected override IEnumerable<Message> WithInitialMessages()
	{
		yield break;
	}

	protected override Message When()
	{
		return StorageMessage.ConsistencyChecksFailed.ForSingleStream(
			InternalCorrId,
			_expectedVersion,
			currentVersion: 0,
			isSoftDeleted: true);
	}

	[Test]
	public void failed_request_message_is_published()
	{
		Assert.That(Produced.ContainsSingle<StorageMessage.RequestCompleted>(
			x => x.CorrelationId == InternalCorrId && x.Success == false));
	}

	[Test]
	public void the_envelope_is_replied_to_with_failure()
	{
		Assert.That(Envelope.Replies.ContainsSingle<ClientMessage.WriteEventsCompleted>(
			x => x.CorrelationId == ClientCorrId &&
				 x.Result == OperationResult.StreamDeleted &&
				 x.ConsistencyCheckFailures.Count == 1 &&
				 x.ConsistencyCheckFailures[0].ExpectedVersion == _expectedVersion &&
				 x.ConsistencyCheckFailures[0].IsSoftDeleted == true));
	}
}
