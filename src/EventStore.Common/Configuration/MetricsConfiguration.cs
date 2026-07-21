using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace EventStore.Common.Configuration;

public class MetricsConfiguration
{
	public static readonly TimeSpan DefaultSlowMessageThreshold = TimeSpan.FromMilliseconds(48);

	public static MetricsConfiguration Get(IConfiguration configuration) =>
		configuration
			.GetSection("EventStore:Metrics")
			.Get<MetricsConfiguration>() ?? new();

	public TimeSpan GetSlowMessageThreshold(string name) =>
		GetSlowMessageThreshold(name, DefaultSlowMessageThreshold);

	public TimeSpan GetSlowMessageThreshold(string name, TimeSpan fallback) =>
		SlowMessageThresholdMilliseconds.TryGetValue(name, out var thresholdMilliseconds)
			? thresholdMilliseconds > 0
				? TimeSpan.FromMilliseconds(thresholdMilliseconds)
				: TimeSpan.Zero
			: fallback;

	public enum StatusTracker
	{
		Index = 1,
		Node,
		Scavenge,
	}

	public enum Checkpoint
	{
		Chaser = 1,
		Epoch,
		Index,
		Proposal,
		Replication,
		StreamExistenceFilter,
		Truncate,
		Writer,
	}

	public enum IncomingGrpcCall
	{
		Current = 1,
		Total,
		Failed,
		Unimplemented,
		DeadlineExceeded,
	}

	public enum GrpcMethod
	{
		StreamRead = 1,
		StreamAppend,
		StreamBatchAppend,
		StreamDelete,
		StreamTombstone,
	}

	public enum GossipTracker
	{
		PullFromPeer = 1,
		PushToPeer,
		ProcessingPushFromPeer,
		ProcessingRequestFromPeer,
		ProcessingRequestFromGrpcClient,
	}

	public enum WriterTracker
	{
		FlushSize = 1,
		FlushDuration,
	}

	public enum EventTracker
	{
		Read = 1,
		Written,
	}

	public enum Cache
	{
		StreamInfo = 1,
		Chunk,
	}

	public enum SystemTracker
	{
		LoadAverage1m = 1,
		LoadAverage5m,
		LoadAverage15m,
		DriveTotalBytes,
		DriveUsedBytes,
	}

	public enum ProcessTracker
	{
		UpTime = 1,
		DiskReadBytes,
		DiskReadOps,
		DiskWrittenBytes,
		DiskWrittenOps,
	}

	public enum QueueTracker
	{
		Busy = 1,
		Length,
		Processing,
	}

	public class LabelMappingCase
	{
		public string Regex { get; set; } = "";
		public string Label { get; set; } = "";
	}

	public class OtlpExporterConfiguration
	{
		public bool Enabled { get; set; }
	}

	public string[] Meters { get; set; } = Array.Empty<string>();

	public Dictionary<StatusTracker, bool> Statuses { get; set; } = new();

	public Dictionary<Checkpoint, bool> Checkpoints { get; set; } = new();

	public Dictionary<IncomingGrpcCall, bool> IncomingGrpcCalls { get; set; } = new();

	public Dictionary<GrpcMethod, string> GrpcMethods { get; set; } = new();

	public Dictionary<GossipTracker, bool> Gossip { get; set; } = new();

	public Dictionary<SystemTracker, bool> System { get; set; } = new();

	public Dictionary<ProcessTracker, bool> Process { get; set; } = new();

	public Dictionary<WriterTracker, bool> Writer { get; set; } = new();

	public Dictionary<EventTracker, bool> Events { get; set; } = new();

	public Dictionary<Cache, bool> CacheHitsMisses { get; set; } = new();

	public bool ProjectionStats { get; set; }

	public bool PersistentSubscriptionStats { get; set; } = false;

	public bool ElectionsCount { get; set; } = false;

	public bool CacheResources { get; set; } = false;

	// must be 0, 1, 5, 10 or a multiple of 15
	public int ExpectedScrapeIntervalSeconds { get; set; }

	public OtlpExporterConfiguration Otlp { get; set; } = new();

	public Dictionary<string, int> SlowMessageThresholdMilliseconds { get; set; } = new();

	public Dictionary<QueueTracker, bool> Queues { get; set; } = new();

	public LabelMappingCase[] QueueLabels { get; set; } = Array.Empty<LabelMappingCase>();

	public LabelMappingCase[] MessageTypes { get; set; } = Array.Empty<LabelMappingCase>();
}
