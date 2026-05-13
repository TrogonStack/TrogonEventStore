using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Enumerators;
using EventStore.Core.Services.UserManagement;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Enumerators;

[TestFixture]
public partial class EnumeratorTests
{
	[Test]
	public async Task read_all_forwards_retries_expired_pages()
	{
		var publishCount = 0;
		var publisher = new ReplyingPublisher(message =>
		{
			var request = (ClientMessage.ReadAllEventsForward)message;
			var response = publishCount++ == 0
				? Expired(request)
				: EndOfStream(request);
			_ = Task.Run(() => request.Envelope.ReplyWith(response));
		});

		await using var enumerator = new Enumerator.ReadAllForwards(
			bus: publisher,
			position: Position.Start,
			maxCount: 1,
			resolveLinks: false,
			user: SystemAccounts.System,
			requiresLeader: false,
			cancellationToken: CancellationToken.None);

		Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)), Is.False);
		Assert.That(publishCount, Is.EqualTo(2));
		Assert.That(((ClientMessage.ReadAllEventsForward)publisher.Published[0]).ReplyOnExpired, Is.True);
	}

	[Test]
	public async Task read_all_backwards_retries_expired_pages()
	{
		var publishCount = 0;
		var publisher = new ReplyingPublisher(message =>
		{
			var request = (ClientMessage.ReadAllEventsBackward)message;
			var response = publishCount++ == 0
				? Expired(request)
				: EndOfStream(request);
			_ = Task.Run(() => request.Envelope.ReplyWith(response));
		});

		await using var enumerator = new Enumerator.ReadAllBackwards(
			bus: publisher,
			position: Position.End,
			maxCount: 1,
			resolveLinks: false,
			user: SystemAccounts.System,
			requiresLeader: false,
			cancellationToken: CancellationToken.None);

		Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)), Is.False);
		Assert.That(publishCount, Is.EqualTo(2));
		Assert.That(((ClientMessage.ReadAllEventsBackward)publisher.Published[0]).ReplyOnExpired, Is.True);
	}

	private static ClientMessage.ReadAllEventsForwardCompleted Expired(ClientMessage.ReadAllEventsForward request) =>
		new(
			request.CorrelationId,
			ReadAllResult.Expired,
			error: default,
			ResolvedEvent.EmptyArray,
			streamMetadata: default,
			isCachePublic: default,
			request.MaxCount,
			currentPos: new TFPos(request.CommitPosition, request.PreparePosition),
			nextPos: TFPos.Invalid,
			prevPos: TFPos.Invalid,
			tfLastCommitPosition: default);

	private static ClientMessage.ReadAllEventsForwardCompleted EndOfStream(ClientMessage.ReadAllEventsForward request) =>
		new(
			request.CorrelationId,
			ReadAllResult.Success,
			error: default,
			ResolvedEvent.EmptyArray,
			streamMetadata: default,
			isCachePublic: default,
			request.MaxCount,
			currentPos: new TFPos(request.CommitPosition, request.PreparePosition),
			nextPos: TFPos.Invalid,
			prevPos: TFPos.Invalid,
			tfLastCommitPosition: default);

	private static ClientMessage.ReadAllEventsBackwardCompleted Expired(ClientMessage.ReadAllEventsBackward request) =>
		new(
			request.CorrelationId,
			ReadAllResult.Expired,
			error: default,
			ResolvedEvent.EmptyArray,
			streamMetadata: default,
			isCachePublic: default,
			request.MaxCount,
			currentPos: new TFPos(request.CommitPosition, request.PreparePosition),
			nextPos: TFPos.Invalid,
			prevPos: TFPos.Invalid,
			tfLastCommitPosition: default);

	private static ClientMessage.ReadAllEventsBackwardCompleted EndOfStream(ClientMessage.ReadAllEventsBackward request) =>
		new(
			request.CorrelationId,
			ReadAllResult.Success,
			error: default,
			ResolvedEvent.EmptyArray,
			streamMetadata: default,
			isCachePublic: default,
			request.MaxCount,
			currentPos: new TFPos(request.CommitPosition, request.PreparePosition),
			nextPos: TFPos.Invalid,
			prevPos: TFPos.Invalid,
			tfLastCommitPosition: default);

	private sealed class ReplyingPublisher(Action<Message> onPublish) : IPublisher
	{
		public List<Message> Published { get; } = new();

		public void Publish(Message message)
		{
			Published.Add(message);
			onPublish(message);
		}
	}
}
