using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Bus;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Enumerators;
using EventStore.Core.Services.UserManagement;
using NUnit.Framework;

namespace EventStore.Core.Tests.Services.Transport.Enumerators;

[TestFixture]
public class SubscriptionDisposalOrderingTests
{
	[TestCase("stream")]
	[TestCase("all")]
	[TestCase("filtered-all")]
	public async Task disposing_while_live_subscription_is_being_registered_does_not_leave_it_active(string kind)
	{
		var publisher = new DelayedSubscriptionPublisher();
		var enumerator = CreateEnumerator(kind, publisher);
		try
		{
			await publisher.SubscribeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			var disposal = enumerator.DisposeAsync().AsTask();
			publisher.ReleaseSubscribe();
			await disposal.WaitAsync(TimeSpan.FromSeconds(10));
			await publisher.SubscribeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

			Assert.That(publisher.ActiveSubscriptions, Is.Empty);
		}
		finally
		{
			publisher.ReleaseSubscribe();
			await enumerator.DisposeAsync();
		}
	}

	private static IAsyncEnumerator<ReadResponse> CreateEnumerator(string kind, IPublisher publisher) => kind switch
	{
		"stream" => new Enumerator.StreamSubscription<string>(
			publisher, new DefaultExpiryStrategy(), "subscription-disposal-ordering", StreamRevision.End,
			false, SystemAccounts.System, false, CancellationToken.None),
		"all" => new Enumerator.AllSubscription(
			publisher, new DefaultExpiryStrategy(), Position.End,
			false, SystemAccounts.System, false, CancellationToken.None),
		"filtered-all" => new Enumerator.AllSubscriptionFiltered(
			publisher, new DefaultExpiryStrategy(), Position.End, false,
			EventFilter.EventType.Prefixes(false, "matching"), SystemAccounts.System, false,
			null, 1, CancellationToken.None),
		_ => throw new ArgumentOutOfRangeException(nameof(kind))
	};

	private sealed class DelayedSubscriptionPublisher : IPublisher
	{
		private readonly ManualResetEventSlim _continueSubscribe = new(false);
		private readonly ConcurrentDictionary<Guid, byte> _activeSubscriptions = new();
		public TaskCompletionSource<bool> SubscribeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> SubscribeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Guid[] ActiveSubscriptions => [.. _activeSubscriptions.Keys];

		public void ReleaseSubscribe() => _continueSubscribe.Set();

		public void Publish(Message message)
		{
			switch (message)
			{
				case ClientMessage.SubscribeToStream subscription:
					Register(subscription.CorrelationId, subscription.Envelope);
					break;
				case ClientMessage.FilteredSubscribeToStream subscription:
					Register(subscription.CorrelationId, subscription.Envelope);
					break;
				case ClientMessage.UnsubscribeFromStream unsubscribe:
					_activeSubscriptions.TryRemove(unsubscribe.CorrelationId, out _);
					break;
			}
		}

		private void Register(Guid id, IEnvelope envelope)
		{
			SubscribeEntered.TrySetResult(true);
			_continueSubscribe.Wait(TimeSpan.FromSeconds(10));
			_activeSubscriptions.TryAdd(id, 0);
			envelope.ReplyWith(new ClientMessage.SubscriptionConfirmation(id, 0, 0));
			SubscribeCompleted.TrySetResult(true);
		}
	}
}
