using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using EventStore.Core.Bus;
using EventStore.Core.Diagnostics;
using EventStore.Core.Index;
using EventStore.Core.Metrics;
using EventStore.Core.Services.VNode;
using EventStore.Core.TransactionLog;
using EventStore.Core.TransactionLog.Checkpoint;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Scavenging;
using TrogonEventStore.SemanticConventions;
using Conf = EventStore.Common.Configuration.MetricsConfiguration;

namespace EventStore.Core;

public class Trackers
{
	public IInaugurationStatusTracker InaugurationStatusTracker { get; set; } = new NodeStatusTracker.NoOp();
	public IIndexStatusTracker IndexStatusTracker { get; set; } = new IndexStatusTracker.NoOp();
	public INodeStatusTracker NodeStatusTracker { get; set; } = new NodeStatusTracker.NoOp();
	public IScavengeStatusTracker ScavengeStatusTracker { get; set; } = new ScavengeStatusTracker.NoOp();
	public GrpcTrackers GrpcTrackers { get; } = new();
	public QueueTrackers QueueTrackers { get; set; } = new();
	public GossipTrackers GossipTrackers { get; set; } = new();
	public ITransactionFileTracker TransactionFileTracker { get; set; } = new TFChunkTracker.NoOp();
	public IIndexTracker IndexTracker { get; set; } = new IndexTracker.NoOp();
	public IMaxTracker<long> WriterFlushSizeTracker { get; set; } = new MaxTracker<long>.NoOp();
	public IDurationMaxTracker WriterFlushDurationTracker { get; set; } = new DurationMaxTracker.NoOp();
	public ICacheHitsMissesTracker CacheHitsMissesTracker { get; set; } = new CacheHitsMissesTracker.NoOp();
	public ICacheResourcesTracker CacheResourcesTracker { get; set; } = new CacheResourcesTracker.NoOp();
	public IElectionCounterTracker ElectionCounterTracker { get; set; } = new ElectionsCounterTracker.NoOp();
	public IPersistentSubscriptionTracker PersistentSubscriptionTracker { get; set; } =
		IPersistentSubscriptionTracker.NoOp;
}

public class GrpcTrackers
{
	private readonly IDurationTracker[] _trackers;

	public GrpcTrackers()
	{
		_trackers = new IDurationTracker[Enum.GetValues<Conf.GrpcMethod>().Cast<int>().Max() + 1];
		var noOp = new DurationTracker.NoOp();
		for (var i = 0; i < _trackers.Length; i++)
		{
			_trackers[i] = noOp;
		}
	}

	public IDurationTracker this[Conf.GrpcMethod index]
	{
		get => _trackers[(int)index];
		set => _trackers[(int)index] = value;
	}
}

public class GossipTrackers
{
	public IDurationTracker PullFromPeer { get; set; } = new DurationTracker.NoOp();
	public IDurationTracker PushToPeer { get; set; } = new DurationTracker.NoOp();
	public IDurationTracker ProcessingPushFromPeer { get; set; } = new DurationTracker.NoOp();
	public IDurationTracker ProcessingRequestFromPeer { get; set; } = new DurationTracker.NoOp();
	public IDurationTracker ProcessingRequestFromGrpcClient { get; set; } = new DurationTracker.NoOp();
}

public static class MetricsBootstrapper
{
	public static void Bootstrap(
		Conf conf,
		TFChunkDbConfig dbConfig,
		Trackers trackers)
	{

		OptionsFormatter.LogConfig("Metrics", conf);

		MessageLabelConfigurator.ConfigureMessageLabels(
			conf.MessageTypes, InMemoryBus.KnownMessageTypes);

		if (conf.ExpectedScrapeIntervalSeconds <= 0)
		{
			return;
		}

		var coreMeter = TelemetryMeterFactory.Create(TelemetryMeterInstrumentation.CoreName);
		var statusMetric = new StatusMetric(coreMeter, MetricDefinitions.TrogonEventstoreComponentStatus);
		var grpcMethodMetric = new DurationMetric(coreMeter, MetricDefinitions.TrogonEventstoreGrpcServerCallDuration);
		var gossipLatencyMetric = new DurationMetric(coreMeter, MetricDefinitions.TrogonEventstoreGossipExchangeDuration);
		var gossipProcessingMetric = new DurationMetric(coreMeter, MetricDefinitions.TrogonEventstoreGossipMessageProcessingDuration);
		var queueQueueingDurationMaxMetric = new DurationMaxMetric(coreMeter, MetricDefinitions.TrogonEventstoreQueueMessageWaitDurationMax);
		var queueProcessingDurationMetric = new DurationMetric(coreMeter, MetricDefinitions.TrogonEventstoreQueueMessageProcessingDuration);
		var queueBusyMetric = new SummedCounterMetric(
			coreMeter,
			MetricDefinitions.TrogonEventstoreQueueBusyTime,
			label => new(TrogonAttributeNames.QueueName, label));
		var queueLengthMetric = new ObservableUpDownMetric<int>(
			coreMeter,
			MetricDefinitions.TrogonEventstoreQueueMessageCount);
		var byteMetric = new CounterMetric(coreMeter, MetricDefinitions.TrogonEventstoreStorageIo);
		var eventMetric = new CounterMetric(coreMeter, MetricDefinitions.TrogonEventstoreStorageEventCount);
		var recordReadDurationMetric = new DurationMetric(coreMeter, MetricDefinitions.TrogonEventstoreStorageRecordReadDuration);
		var electionsCounterMetric = new CounterMetric(coreMeter, MetricDefinitions.TrogonEventstoreClusterElectionCount);

		// incoming grpc calls
		var enabledCalls = conf.IncomingGrpcCalls.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();
		if (enabledCalls.Length > 0)
		{
			_ = new IncomingGrpcCallsMetric(
				coreMeter,
				MetricDefinitions.TrogonEventstoreGrpcServerCallActive,
				MetricDefinitions.TrogonEventstoreGrpcServerCallCount,
				MetricDefinitions.TrogonEventstoreGrpcServerCallFailureCount,
				MetricDefinitions.TrogonEventstoreGrpcServerCallUnimplementedCount,
				MetricDefinitions.TrogonEventstoreGrpcServerCallDeadlineExceededCount,
				enabledCalls);
		}

		// cache hits/misses
		var enabledCacheHitsMisses = conf.CacheHitsMisses.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToArray();
		if (enabledCacheHitsMisses.Length > 0)
		{
			var metric = new CacheHitsMissesMetric(
				coreMeter,
				enabledCacheHitsMisses,
				MetricDefinitions.TrogonEventstoreCacheOperationCount,
				new() {
				{ Conf.Cache.StreamInfo, "stream-info" },
				{ Conf.Cache.Chunk, "chunk" },
			});
			trackers.CacheHitsMissesTracker = new CacheHitsMissesTracker(metric);
		}

		// dynamic cache resources
		if (conf.CacheResources)
		{
			var metrics = new CacheResourcesMetrics(
				coreMeter,
				MetricDefinitions.TrogonEventstoreCacheResourceSize,
				MetricDefinitions.TrogonEventstoreCacheResourceCount);
			trackers.CacheResourcesTracker = new CacheResourcesTracker(metrics);
		}

		// elections count
		if (conf.ElectionsCount)
		{
			trackers.ElectionCounterTracker = new ElectionsCounterTracker(new CounterSubMetric(electionsCounterMetric, []));
		}

		// events
		if (conf.Events.TryGetValue(Conf.EventTracker.Read, out var readEnabled) && readEnabled)
		{
			var readTag = new KeyValuePair<string, object>(TrogonAttributeNames.StorageActivity, "read");
			trackers.TransactionFileTracker = new TFChunkTracker(
				readDistribution: new LogicalChunkReadDistributionMetric(
					meter: coreMeter,
					definition: MetricDefinitions.TrogonEventstoreStorageChunkReadDistance,
					writer: dbConfig.WriterCheckpoint,
					chunkSize: dbConfig.ChunkSize),
				readDurationMetric: recordReadDurationMetric,
				readBytes: new CounterSubMetric(byteMetric, [readTag]),
				readEvents: new CounterSubMetric(eventMetric, [readTag]));
		}

		// from a users perspective an event is written when it is indexed: thats when it can be read.
		if (conf.Events.TryGetValue(Conf.EventTracker.Written, out var writtenEnabled) && writtenEnabled)
		{
			trackers.IndexTracker = new IndexTracker(new CounterSubMetric(
				eventMetric,
				new[] { new KeyValuePair<string, object>(TrogonAttributeNames.StorageActivity, "write") }));
		}

		// gossip
		if (conf.Gossip.Count != 0)
		{
			if (conf.Gossip.TryGetValue(Conf.GossipTracker.PullFromPeer, out var pullFromPeer) && pullFromPeer)
			{
				trackers.GossipTrackers.PullFromPeer = new DurationTracker(gossipLatencyMetric, "pull-from-peer");
			}

			if (conf.Gossip.TryGetValue(Conf.GossipTracker.PushToPeer, out var pushToPeer) && pushToPeer)
			{
				trackers.GossipTrackers.PushToPeer = new DurationTracker(gossipLatencyMetric, "push-to-peer");
			}

			if (conf.Gossip.TryGetValue(Conf.GossipTracker.ProcessingPushFromPeer, out var processingPushFromPeer) && processingPushFromPeer)
			{
				trackers.GossipTrackers.ProcessingPushFromPeer = new DurationTracker(gossipProcessingMetric, "push-from-peer");
			}

			if (conf.Gossip.TryGetValue(Conf.GossipTracker.ProcessingRequestFromPeer, out var processingRequestFromPeer) && processingRequestFromPeer)
			{
				trackers.GossipTrackers.ProcessingRequestFromPeer = new DurationTracker(gossipProcessingMetric, "request-from-peer");
			}

			if (conf.Gossip.TryGetValue(Conf.GossipTracker.ProcessingRequestFromGrpcClient, out var processingRequestFromGrpcClient) && processingRequestFromGrpcClient)
			{
				trackers.GossipTrackers.ProcessingRequestFromGrpcClient = new DurationTracker(gossipProcessingMetric, "request-from-grpc-client");
			}
		}

		// persistent subscriptions
		if (conf.PersistentSubscriptionStats)
		{
			var tracker = new PersistentSubscriptionTracker();
			trackers.PersistentSubscriptionTracker = tracker;

			CreateObservableUpDownCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionConnectionCount, tracker.ObserveConnectionsCount);
			CreateObservableUpDownCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionParkedMessageCount, tracker.ObserveParkedMessages);
			CreateObservableUpDownCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionInFlightMessageCount, tracker.ObserveInFlightMessages);
			CreateObservableGauge(MetricDefinitions.TrogonEventstorePersistentSubscriptionOldestParkedMessageAge, tracker.ObserveOldestParkedMessage);

			CreateObservableCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionParkRequestCount, tracker.ObserveParkMessageRequests);
			CreateObservableCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionParkedMessageReplayCount, tracker.ObserveParkedMessageReplays);
			CreateObservableCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionParkedMessageTruncateCount, tracker.ObserveParkedMessageTruncates);
			CreateObservableCounter(MetricDefinitions.TrogonEventstorePersistentSubscriptionItemProcessedCount, tracker.ObserveItemsProcessed);
			CreateObservableGauge(MetricDefinitions.TrogonEventstorePersistentSubscriptionLastKnownEventRevision, tracker.ObserveLastKnownEvent);
			CreateObservableGauge(MetricDefinitions.TrogonEventstorePersistentSubscriptionLastKnownCommitPosition, tracker.ObserveLastKnownEventCommitPosition);
			CreateObservableGauge(MetricDefinitions.TrogonEventstorePersistentSubscriptionCheckpointEventRevision, tracker.ObserveLastCheckpointedEvent);
			CreateObservableGauge(MetricDefinitions.TrogonEventstorePersistentSubscriptionCheckpointCommitPosition, tracker.ObserveLastCheckpointedEventCommitPosition);

			void CreateObservableCounter(MetricDefinition definition, Func<IEnumerable<Measurement<long>>> observe) =>
				coreMeter.CreateObservableCounter(definition.Name, observe, definition.Unit, definition.Description);

			void CreateObservableGauge(MetricDefinition definition, Func<IEnumerable<Measurement<long>>> observe) =>
				coreMeter.CreateObservableGauge(definition.Name, observe, definition.Unit, definition.Description);

			void CreateObservableUpDownCounter(MetricDefinition definition, Func<IEnumerable<Measurement<long>>> observe) =>
				coreMeter.CreateObservableUpDownCounter(definition.Name, observe, definition.Unit, definition.Description);
		}

		// checkpoints
		_ = new CheckpointMetric(
			coreMeter,
			MetricDefinitions.TrogonEventstoreCheckpointPosition,
			conf.Checkpoints.Where(x => x.Value).Select(x => x.Key switch
			{
				Conf.Checkpoint.Chaser => dbConfig.ChaserCheckpoint,
				Conf.Checkpoint.Epoch => dbConfig.EpochCheckpoint,
				Conf.Checkpoint.Index => dbConfig.IndexCheckpoint,
				Conf.Checkpoint.Proposal => dbConfig.ProposalCheckpoint,
				Conf.Checkpoint.Replication => dbConfig.ReplicationCheckpoint,
				Conf.Checkpoint.StreamExistenceFilter => dbConfig.StreamExistenceFilterCheckpoint,
				Conf.Checkpoint.Truncate => dbConfig.TruncateCheckpoint,
				Conf.Checkpoint.Writer => dbConfig.WriterCheckpoint,
				_ => throw new Exception(
					$"Unknown checkpoint in MetricsConfiguration. Valid values are " +
					$"{string.Join(", ", Enum.GetValues<Conf.Checkpoint>())}"),
			}).ToArray());

		// status metrics
		if (conf.Statuses.Count > 0)
		{
			if (conf.Statuses.TryGetValue(Conf.StatusTracker.Node, out var nodeStatus) && nodeStatus)
			{
				var tracker = new NodeStatusTracker(statusMetric);
				trackers.NodeStatusTracker = tracker;
				trackers.InaugurationStatusTracker = tracker;
			}

			if (conf.Statuses.TryGetValue(Conf.StatusTracker.Index, out var indexStatus) && indexStatus)
			{
				trackers.IndexStatusTracker = new IndexStatusTracker(statusMetric);
			}

			if (conf.Statuses.TryGetValue(Conf.StatusTracker.Scavenge, out var scavengeStatus) && scavengeStatus)
			{
				trackers.ScavengeStatusTracker = new ScavengeStatusTracker(statusMetric);
			}
		}

		// grpc historgrams
		foreach (var method in Enum.GetValues<Conf.GrpcMethod>())
		{
			if (conf.GrpcMethods.TryGetValue(method, out var label) && !string.IsNullOrWhiteSpace(label))
			{
				trackers.GrpcTrackers[method] = new DurationTracker(grpcMethodMetric, label);
			}
		}

		// storage writer
		if (conf.Writer.Count > 0)
		{
			if (conf.Writer.TryGetValue(Conf.WriterTracker.FlushSize, out var flushSizeEnabled) && flushSizeEnabled)
			{
				var maxMetric = new MaxMetric<long>(coreMeter, MetricDefinitions.TrogonEventstoreWriterFlushSizeMax);
				trackers.WriterFlushSizeTracker = new MaxTracker<long>(
					metric: maxMetric,
					name: null,
					expectedScrapeIntervalSeconds: conf.ExpectedScrapeIntervalSeconds);
			}

			if (conf.Writer.TryGetValue(Conf.WriterTracker.FlushDuration, out var flushDurationEnabled) && flushDurationEnabled)
			{
				var maxDurationmetric = new DurationMaxMetric(coreMeter, MetricDefinitions.TrogonEventstoreWriterFlushDurationMax);
				trackers.WriterFlushDurationTracker = new DurationMaxTracker(
					maxDurationmetric,
					name: null,
					expectedScrapeIntervalSeconds: conf.ExpectedScrapeIntervalSeconds);
			}
		}

		// queue trackers
		Func<string, IQueueBusyTracker> busyTrackerFactory = name => new QueueBusyTracker.NoOp();
		Func<string, IQueueLengthTracker> lengthTrackerFactory = name => new QueueLengthTracker.NoOp();
		Func<string, IDurationMaxTracker> queueingDurationFactory = name => new DurationMaxTracker.NoOp();
		Func<string, IQueueProcessingTracker> processingFactory = name => new QueueProcessingTracker.NoOp();

		if (conf.Queues.TryGetValue(Conf.QueueTracker.Busy, out var busyEnabled) && busyEnabled)
		{
			busyTrackerFactory = name => new QueueBusyTracker(queueBusyMetric, name);
		}

		if (conf.Queues.TryGetValue(Conf.QueueTracker.Length, out var lengthEnabled) && lengthEnabled)
		{
			lengthTrackerFactory = name => new QueueLengthTracker(queueLengthMetric, name);
			queueingDurationFactory = name => new DurationMaxTracker(
				name: name,
				metric: queueQueueingDurationMaxMetric,
				expectedScrapeIntervalSeconds: conf.ExpectedScrapeIntervalSeconds);
		}

		if (conf.Queues.TryGetValue(Conf.QueueTracker.Processing, out var processingEnabled) && processingEnabled)
		{
			processingFactory = name => new QueueProcessingTracker(queueProcessingDurationMetric, name);
		}

		trackers.QueueTrackers = new QueueTrackers(
			conf.QueueLabels,
			busyTrackerFactory,
			lengthTrackerFactory,
			queueingDurationFactory,
			processingFactory);

		var timeout = TimeSpan.FromSeconds(1);

		// system
		var systemMetrics = new SystemMetrics(coreMeter, timeout, conf.System);
		systemMetrics.CreateLoadAverageMetric();
		systemMetrics.CreateFileSystemMetrics(dbConfig.Path);

		// process
		var processMetrics = new ProcessMetrics(coreMeter, timeout, conf.Process);
		processMetrics.CreateUptimeMetric();
		processMetrics.CreateDiskIoMetric();
		processMetrics.CreateDiskOperationMetric();
	}
}
