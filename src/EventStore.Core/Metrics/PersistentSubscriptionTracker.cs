using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Common.Utils;
using EventStore.Core.Messages;
using TrogonEventStore.SemanticConventions;

namespace EventStore.Core.Metrics;

public class PersistentSubscriptionTracker : IPersistentSubscriptionTracker
{
	private IReadOnlyList<MonitoringMessage.PersistentSubscriptionInfo> _currentStats = [];

	public void OnNewStats(IReadOnlyList<MonitoringMessage.PersistentSubscriptionInfo> newStats)
	{
		_currentStats = newStats ?? [];
	}

	public IEnumerable<Measurement<long>> ObserveConnectionsCount() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.Connections.Count, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)]));

	public IEnumerable<Measurement<long>> ObserveParkedMessages() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.ParkedMessageCount, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
			]));

	public IEnumerable<Measurement<long>> ObserveParkMessageRequests() =>
		_currentStats.SelectMany<MonitoringMessage.PersistentSubscriptionInfo, Measurement<long>>(x => [
			new(x.ParkedDueToClientNak, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName),
				new(TrogonAttributeNames.PersistentSubscriptionReason, "client_nack")
			]),
			new(x.ParkedDueToMaxRetries, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName),
				new(TrogonAttributeNames.PersistentSubscriptionReason, "max_retries")
			])
		]);

	public IEnumerable<Measurement<long>> ObserveParkedMessageReplays() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.ParkedMessageReplays, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
			]));

	public IEnumerable<Measurement<long>> ObserveParkedMessageTruncates() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.ParkedMessageTruncates, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
			]));

	public IEnumerable<Measurement<long>> ObserveInFlightMessages() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.TotalInFlightMessages, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
			]));

	public IEnumerable<Measurement<long>> ObserveOldestParkedMessage() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.OldestParkedMessage, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
			]));

	public IEnumerable<Measurement<long>> ObserveItemsProcessed() =>
		_currentStats.Select(x =>
			new Measurement<long>(x.TotalItems, [
				new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
				new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
			]));

	public IEnumerable<Measurement<long>> ObserveLastKnownEvent() =>
		_currentStats
			.Where(x => x.EventSource != "$all")
			.Select(x =>
			{
				var measurement = long.TryParse(x.LastKnownEventPosition, out var lastEventPos)
					? lastEventPos
					: 0;
				return new Measurement<long>(measurement, [
					new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
					new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
				]);
			});

	public IEnumerable<Measurement<long>> ObserveLastKnownEventCommitPosition() =>
		_currentStats
			.Where(x => x.EventSource == "$all")
			.Select(x =>
			{
				var (eventCommitPosition, _) = EventPositionParser.ParseCommitPreparePosition(x.LastKnownEventPosition);
				return new Measurement<long>(eventCommitPosition, [
					new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
					new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
				]);
			});

	public IEnumerable<Measurement<long>> ObserveLastCheckpointedEvent() =>
		_currentStats
			.Where(x => x.EventSource != "$all")
			.Select(x =>
			{
				var measurement = long.TryParse(x.LastCheckpointedEventPosition, out var lastEventPos)
					? lastEventPos
					: 0;
				return new Measurement<long>(measurement, [
					new(TrogonAttributeNames.PersistentSubscriptionStream, x.EventSource),
					new(TrogonAttributeNames.PersistentSubscriptionGroup, x.GroupName)
				]);
			});

	public IEnumerable<Measurement<long>> ObserveLastCheckpointedEventCommitPosition() =>
		_currentStats
			.Where(x => x.EventSource == "$all")
			.Select(statistics =>
			{
				var (checkpointedCommitPosition, _) = EventPositionParser.ParseCommitPreparePosition(statistics.LastCheckpointedEventPosition);
				return new Measurement<long>(checkpointedCommitPosition, [
					new(TrogonAttributeNames.PersistentSubscriptionStream, statistics.EventSource),
					new(TrogonAttributeNames.PersistentSubscriptionGroup, statistics.GroupName)
				]);
			});
}
