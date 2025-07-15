using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Services.UserManagement;
using Serilog;

namespace EventStore.Core.Services.PersistentSubscription {
	public class PersistentSubscriptionStreamReader(IODispatcher ioDispatcher, int maxPullBatchSize)
		: IPersistentSubscriptionStreamReader
	{
		private static readonly ILogger Log = Serilog.Log.ForContext<PersistentSubscriptionStreamReader>();

		public const int MaxPullBatchSize = 500;
		private const int MaxRetryTimeSecs = 60;
		private const int MaxExponentialBackoffPower = 10; /*to prevent integer overflow*/

		private readonly Random _random = new Random();

		public void BeginReadEvents(IPersistentSubscriptionEventSource eventSource,
			IPersistentSubscriptionStreamPosition startPosition, int countToLoad, int batchSize,
			int maxWindowSize, bool resolveLinkTos, bool skipFirstEvent,
			Action<IReadOnlyList<ResolvedEvent>, IPersistentSubscriptionStreamPosition, bool> onEventsFound,
			Action<IPersistentSubscriptionStreamPosition, long> onEventsSkipped,
			Action<string> onError) {
			BeginReadEventsInternal(eventSource, startPosition, countToLoad, batchSize, maxWindowSize, resolveLinkTos,
				skipFirstEvent, onEventsFound, onEventsSkipped, onError, 0);
		}

		private int GetBackOffDelay(int retryCount) {
			//exponential backoff + jitter
			return 1 + _random.Next(0, 1 + Math.Min(MaxRetryTimeSecs, (1 << Math.Min(retryCount, MaxExponentialBackoffPower))));
		}

		private void BeginReadEventsInternal(IPersistentSubscriptionEventSource eventSource,
			IPersistentSubscriptionStreamPosition startPosition, int countToLoad, int batchSize, int maxWindowSize, bool resolveLinkTos, bool skipFirstEvent,
			Action<IReadOnlyList<ResolvedEvent>, IPersistentSubscriptionStreamPosition, bool> onEventsFound,
			Action<IPersistentSubscriptionStreamPosition, long> onEventsSkipped,
			Action<string> onError, int retryCount) {
			var actualBatchSize = GetBatchSize(batchSize);

			if (eventSource.FromStream) {
				ioDispatcher.ReadForward(
					eventSource.EventStreamId, startPosition.StreamEventNumber, Math.Min(countToLoad, actualBatchSize),
					resolveLinkTos, SystemAccounts.System, new ResponseHandler(onEventsFound, onEventsSkipped, onError, skipFirstEvent).FetchCompleted,
					async () => await HandleTimeout(eventSource.EventStreamId),
					Guid.NewGuid());
			} else if (eventSource.FromAll) {
				if (eventSource.EventFilter is null) {
					ioDispatcher.ReadAllForward(
						startPosition.TFPosition.Commit,
						startPosition.TFPosition.Prepare,
						Math.Min(countToLoad, actualBatchSize),
						resolveLinkTos,
						true,
						null,
						SystemAccounts.System,
						null,
						new ResponseHandler(onEventsFound, onEventsSkipped, onError, skipFirstEvent).FetchAllCompleted,
						async () => await HandleTimeout(SystemStreams.AllStream),
						Guid.NewGuid());
				} else {
					var maxSearchWindow = Math.Max(actualBatchSize, maxWindowSize);
					ioDispatcher.ReadAllForwardFiltered(
						startPosition.TFPosition.Commit,
						startPosition.TFPosition.Prepare,
						Math.Min(countToLoad, actualBatchSize),
						resolveLinkTos,
						true,
						maxSearchWindow,
						null,
						eventSource.EventFilter,
						SystemAccounts.System,
						null,
						new ResponseHandler(onEventsFound, onEventsSkipped, onError, skipFirstEvent).FetchAllFilteredCompleted,
						async () => await HandleTimeout($"{SystemStreams.AllStream} with filter {eventSource.EventFilter}"),
						Guid.NewGuid());
				}
			} else {
				throw new InvalidOperationException();
			}

			async Task HandleTimeout(string streamName) {
				var backOff = GetBackOffDelay(retryCount);
				Log.Warning(
					"Timed out reading from stream: {stream}. Retrying in {retryInterval} seconds.",
					streamName, backOff);
				await Task.Delay(TimeSpan.FromSeconds(backOff));
				BeginReadEventsInternal(eventSource, startPosition, countToLoad, batchSize, maxWindowSize, resolveLinkTos,
					skipFirstEvent, onEventsFound, onEventsSkipped, onError, retryCount + 1);
			}
		}

		private int GetBatchSize(int batchSize) {
			return Math.Min(Math.Min(batchSize == 0 ? 20 : batchSize, MaxPullBatchSize), maxPullBatchSize);
		}

		private class ResponseHandler(
			Action<IReadOnlyList<ResolvedEvent>, IPersistentSubscriptionStreamPosition, bool> onFetchCompleted,
			Action<IPersistentSubscriptionStreamPosition, long> onEventsSkipped,
			Action<string> onError,
			bool skipFirstEvent)
		{
			public void FetchCompleted(ClientMessage.ReadStreamEventsForwardCompleted msg) {
				switch (msg.Result) {
					case ReadStreamResult.Success:
						onFetchCompleted(skipFirstEvent ? msg.Events.Skip(1).ToArray() : msg.Events,
							new PersistentSubscriptionSingleStreamPosition(msg.NextEventNumber), msg.IsEndOfStream);
						break;
					case ReadStreamResult.NoStream:
						onFetchCompleted(
							[],
							new PersistentSubscriptionSingleStreamPosition(0),
							msg.IsEndOfStream);
						break;
					case ReadStreamResult.AccessDenied:
						onError($"Read access denied for stream: {msg.EventStreamId}");
						break;
					default:
						onError(msg.Error ?? $"Error reading stream: {msg.EventStreamId} at event number: {msg.FromEventNumber}");
						break;
				}
			}

			public void FetchAllCompleted(ClientMessage.ReadAllEventsForwardCompleted msg) {
				switch (msg.Result) {
					case ReadAllResult.Success:
						onFetchCompleted(skipFirstEvent ? msg.Events.Skip(1).ToArray() : msg.Events,
							new PersistentSubscriptionAllStreamPosition(msg.NextPos.CommitPosition, msg.NextPos.PreparePosition), msg.IsEndOfStream);
						break;
					case ReadAllResult.AccessDenied:
						onError($"Read access denied for stream: {SystemStreams.AllStream}");
						break;
					default:
						onError(msg.Error ?? $"Error reading stream: {SystemStreams.AllStream} at position: {msg.CurrentPos}");
						break;
				}
			}
			public void FetchAllFilteredCompleted(ClientMessage.FilteredReadAllEventsForwardCompleted msg) {
				switch (msg.Result) {
					case FilteredReadAllResult.Success:
						if (msg.Events is [] && msg.ConsideredEventsCount > 0) {
							// Checkpoint on the position we read from rather than the next position
							// to prevent skipping the next event when loading from the checkpoint
							onEventsSkipped(
								new PersistentSubscriptionAllStreamPosition(msg.CurrentPos.CommitPosition, msg.CurrentPos.PreparePosition),
								msg.ConsideredEventsCount);
						}
						onFetchCompleted(skipFirstEvent ? msg.Events.Skip(1).ToArray() : msg.Events,
							new PersistentSubscriptionAllStreamPosition(msg.NextPos.CommitPosition, msg.NextPos.PreparePosition), msg.IsEndOfStream);

						break;
					case FilteredReadAllResult.AccessDenied:
						onError($"Read access denied for stream: {SystemStreams.AllStream}");
						break;
					default:
						onError(msg.Error ?? $"Error reading stream: {SystemStreams.AllStream} at position: {msg.CurrentPos}");
						break;
				}
			}
		}
	}
}
