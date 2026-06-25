using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Services.Storage.Indexing;
using EventStore.Core.Services.Transport.Common;
using EventStore.Core.Services.Transport.Enumerators;
using EventStore.Core.TransactionLog.LogRecords;
using Xunit;

namespace EventStore.Core.XUnit.Tests.Services.Storage.Indexing;

public class IndexingSubscriptionTests
{
	[Fact]
	public async Task starts_from_component_checkpoint()
	{
		var checkpoint = new IndexCheckpoint(10, 5);
		var component = new FakeIndexingComponent(checkpoint);
		var eventSource = new FakeIndexingEventSource();
		var eventSources = new FakeIndexingEventSourceFactory(eventSource);
		await using var subscription = new IndexingSubscription(
			component,
			eventSources,
			IndexingSubscriptionOptions.Default);

		await subscription.Start(CancellationToken.None);

		Assert.Equal(checkpoint, eventSources.Checkpoint);
	}

	[Fact]
	public async Task can_start_again_after_startup_failure()
	{
		var component = new FakeIndexingComponent(initializeFailures: 1);
		var eventSource = new FakeIndexingEventSource();
		await using var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			IndexingSubscriptionOptions.Default);

		await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.Start(CancellationToken.None).AsTask());

		await subscription.Start(CancellationToken.None);
	}

	[Fact]
	public async Task indexes_events_from_source()
	{
		var first = CreateResolvedEvent(1);
		var second = CreateResolvedEvent(2);
		var component = new FakeIndexingComponent();
		var eventSource = new FakeIndexingEventSource(
			new ReadResponse.EventReceived(first),
			new ReadResponse.SubscriptionCaughtUp(new TFPos(1, 1)),
			new ReadResponse.EventReceived(second));
		await using var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			new IndexingSubscriptionOptions(2, TimeSpan.FromSeconds(30)));

		await subscription.Start(CancellationToken.None);
		await component.Processor.WaitForIndexed(2);

		Assert.Equal(new[] { first, second }, component.Processor.Indexed);
	}

	[Fact]
	public async Task commits_when_batch_size_is_reached()
	{
		var component = new FakeIndexingComponent();
		var eventSource = new FakeIndexingEventSource(
			new ReadResponse.EventReceived(CreateResolvedEvent(1)),
			new ReadResponse.EventReceived(CreateResolvedEvent(2)));
		await using var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			new IndexingSubscriptionOptions(2, TimeSpan.FromSeconds(30)));

		await subscription.Start(CancellationToken.None);
		await component.Processor.WaitForCommits(1);

		Assert.Equal(1, component.Processor.CommitCount);
	}

	[Fact]
	public async Task commits_when_delay_elapses()
	{
		var component = new FakeIndexingComponent();
		var eventSource = new FakeIndexingEventSource(new ReadResponse.EventReceived(CreateResolvedEvent(1)));
		await using var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			new IndexingSubscriptionOptions(100, TimeSpan.FromMilliseconds(25)));

		await subscription.Start(CancellationToken.None);
		await component.Processor.WaitForCommits(1);

		Assert.Equal(1, component.Processor.CommitCount);
	}

	[Fact]
	public async Task commits_pending_events_when_disposed()
	{
		var component = new FakeIndexingComponent();
		var eventSource = new FakeIndexingEventSource(new ReadResponse.EventReceived(CreateResolvedEvent(1)));
		var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			new IndexingSubscriptionOptions(100, TimeSpan.FromSeconds(30)));

		await subscription.Start(CancellationToken.None);
		await component.Processor.WaitForIndexed(1);
		await subscription.DisposeAsync();

		Assert.Equal(1, component.Processor.CommitCount);
		Assert.True(component.Disposed);
		Assert.True(eventSource.Disposed);
	}

	[Fact]
	public async Task commits_in_flight_event_when_disposed()
	{
		var component = new FakeIndexingComponent(pauseIndexCompletion: true);
		var eventSource = new FakeIndexingEventSource(new ReadResponse.EventReceived(CreateResolvedEvent(1)));
		var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			new IndexingSubscriptionOptions(100, TimeSpan.FromSeconds(30)));

		await subscription.Start(CancellationToken.None);
		await component.Processor.WaitForIndexEntered();
		var disposal = subscription.DisposeAsync().AsTask();

		component.Processor.ReleaseIndex();
		await disposal.WaitAsync(Timeout);

		Assert.Equal(1, component.Processor.CommitCount);
		Assert.True(component.Disposed);
		Assert.True(eventSource.Disposed);
	}

	[Fact]
	public async Task cleans_up_after_event_source_completion_faults_worker()
	{
		var component = new FakeIndexingComponent();
		var eventSource = new FakeIndexingEventSource(completeWhenDrained: true);
		var subscription = new IndexingSubscription(
			component,
			new FakeIndexingEventSourceFactory(eventSource),
			IndexingSubscriptionOptions.Default);

		await subscription.Start(CancellationToken.None);
		await eventSource.WaitForDrained();
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => subscription.DisposeAsync().AsTask());

		Assert.Contains("completed unexpectedly", exception.Message);
		Assert.True(component.Disposed);
		Assert.True(eventSource.Disposed);
	}

	private static ResolvedEvent CreateResolvedEvent(long number)
	{
		var record = new EventRecord(
			number,
			number,
			Guid.NewGuid(),
			Guid.NewGuid(),
			number,
			0,
			$"stream-{number}",
			number - 1,
			DateTime.UtcNow,
			PrepareFlags.SingleWrite | PrepareFlags.IsCommitted | PrepareFlags.Data,
			$"event-{number}",
			Array.Empty<byte>(),
			Array.Empty<byte>());

		return ResolvedEvent.ForUnresolvedEvent(record, number);
	}

	private sealed class FakeIndexingComponent(
		IndexCheckpoint? checkpoint = null,
		int initializeFailures = 0,
		bool pauseIndexCompletion = false) : IIndexingComponent
	{
		private int _initializeFailures = initializeFailures;

		public FakeIndexingProcessor Processor { get; } = new(pauseIndexCompletion);

		IIndexingProcessor IIndexingComponent.Processor => Processor;

		public bool Disposed { get; private set; }

		public ValueTask Initialize(CancellationToken token)
		{
			if (Interlocked.Decrement(ref _initializeFailures) >= 0)
			{
				throw new InvalidOperationException("initialize failed");
			}

			return ValueTask.CompletedTask;
		}

		public ValueTask<IndexCheckpoint?> ReadCheckpoint(CancellationToken token) => ValueTask.FromResult(checkpoint);

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeIndexingProcessor(bool pauseIndexCompletion = false) : IIndexingProcessor
	{
		private readonly List<ResolvedEvent> _indexed = [];
		private readonly TaskCompletionSource _indexedEvents = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _indexEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _releaseIndex = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _commitCount;
		private int _waitForIndexed;
		private int _waitForCommits;

		public IReadOnlyList<ResolvedEvent> Indexed => _indexed;

		public int CommitCount => Volatile.Read(ref _commitCount);

		public async ValueTask Index(ResolvedEvent resolvedEvent, CancellationToken token)
		{
			lock (_indexed)
			{
				_indexed.Add(resolvedEvent);
				if (_indexed.Count >= Volatile.Read(ref _waitForIndexed))
				{
					_indexedEvents.TrySetResult();
				}
			}

			if (pauseIndexCompletion)
			{
				_indexEntered.TrySetResult();
				await _releaseIndex.Task.WaitAsync(token);
			}
		}

		public ValueTask Commit(CancellationToken token)
		{
			if (Interlocked.Increment(ref _commitCount) >= Volatile.Read(ref _waitForCommits))
			{
				_committed.TrySetResult();
			}

			return ValueTask.CompletedTask;
		}

		public Task WaitForIndexed(int count)
		{
			lock (_indexed)
			{
				_waitForIndexed = count;
				if (_indexed.Count >= count)
				{
					return Task.CompletedTask;
				}
			}

			return _indexedEvents.Task.WaitAsync(Timeout);
		}

		public Task WaitForCommits(int count)
		{
			_waitForCommits = count;
			return CommitCount >= count
				? Task.CompletedTask
				: _committed.Task.WaitAsync(Timeout);
		}

		public Task WaitForIndexEntered() => _indexEntered.Task.WaitAsync(Timeout);

		public void ReleaseIndex() => _releaseIndex.TrySetResult();
	}

	private sealed class FakeIndexingEventSourceFactory(FakeIndexingEventSource source) : IIndexingEventSourceFactory
	{
		public IndexCheckpoint? Checkpoint { get; private set; }

		public IIndexingEventSource Create(IndexCheckpoint? checkpoint, CancellationToken token)
		{
			Checkpoint = checkpoint;
			source.Bind(token);
			return source;
		}
	}

	private sealed class FakeIndexingEventSource(params ReadResponse[] responses) : IIndexingEventSource
	{
		private readonly Queue<ReadResponse> _responses = new(responses);
		private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly bool _completeWhenDrained;
		private CancellationToken _token;

		public FakeIndexingEventSource(bool completeWhenDrained, params ReadResponse[] responses) : this(responses)
		{
			_completeWhenDrained = completeWhenDrained;
		}

		public ReadResponse Current { get; private set; }

		public bool Disposed { get; private set; }

		public void Bind(CancellationToken token) => _token = token;

		public Task WaitForDrained() => _drained.Task.WaitAsync(Timeout);

		public async ValueTask<bool> MoveNextAsync()
		{
			if (!_responses.TryDequeue(out var response))
			{
				if (_completeWhenDrained)
				{
					_drained.TrySetResult();
					return false;
				}

				await Task.Delay(global::System.Threading.Timeout.InfiniteTimeSpan, _token);
				return false;
			}

			Current = response;
			return true;
		}

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return ValueTask.CompletedTask;
		}
	}

	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
}
