<!-- markdownlint-disable MD013 -->

# Metrics reference

TrogonEventStore exposes metrics through the OpenTelemetry Protocol (OTLP) and the Prometheus-compatible `/-/metrics` endpoint. The canonical instrument identity is the dotted OpenTelemetry name documented here. Prometheus exporters may translate dots, units, or other name components according to their configured translation strategy.

Configure node-specific metrics in `metricsconfig.json`. The `Meters` array adds third-party `System.Diagnostics.Metrics` meter sources; the built-in sources do not need to be listed there.

Units use UCUM notation: `s` is seconds, `By` is bytes, and `1` is dimensionless. Units such as `{event}` are annotated counts. The unit is metadata and is not repeated in the canonical instrument name.

Telemetry resources include service identity, process creation time and PID, executable and runtime identity, and host name and architecture. Deployments should provide `host.id` through `OTEL_RESOURCE_ATTRIBUTES` when a stable machine or cloud instance identifier is available.

## Built-in meter sources

The meter provider always subscribes to these sources:

| Meter | Source |
| --- | --- |
| `EventStore.Core` | Core TrogonEventStore instruments |
| `EventStore.Projections.Core` | Projection instruments |
| `System.Runtime` | Native .NET process, garbage collection, exception, lock, and thread pool instruments |
| `Microsoft.AspNetCore.Server.Kestrel` | Native Kestrel connection and server instruments |

The exact native runtime and Kestrel instrument set is determined by the installed .NET runtime. TrogonEventStore does not duplicate those signals under custom names.

For example, GC pause telemetry is the native cumulative `dotnet.gc.pause.time` Counter in seconds. Derive pause rates and alert thresholds in the telemetry backend rather than relying on a node-specific rolling maximum.

## Standard instruments

These instruments use OpenTelemetry semantic convention names.

| Instrument | Kind | Unit | Attributes | Description |
| --- | --- | --- | --- | --- |
| `process.uptime` | Gauge | `s` | None | Time the process has been running |
| `process.disk.io` | Counter | `By` | `disk.io.direction` | Disk bytes transferred by the process; direction is `read` or `write` |
| `system.filesystem.usage` | UpDownCounter | `By` | `system.filesystem.state`, `system.filesystem.mountpoint` | Used and free space on the filesystem containing the database; state is `used` or `free` |
| `system.filesystem.limit` | UpDownCounter | `By` | `system.filesystem.mountpoint` | Total capacity of the filesystem containing the database |

## TrogonEventStore instruments

All `trogon.eventstore.*` instruments are development semantic conventions. Attribute names are part of each instrument contract and should be used instead of parsing instrument names.

### Runtime and operations

| Instrument | Kind | Unit | Attributes | Description |
| --- | --- | --- | --- | --- |
| `trogon.eventstore.component.status` | UpDownCounter | `1` | `trogon.eventstore.component.name`, `trogon.eventstore.component.status` | Current component status represented by one active series with value `1` and inactive series with value `0` |
| `trogon.eventstore.grpc.server.call.duration` | Histogram | `s` | `trogon.eventstore.activity.name`, `trogon.eventstore.activity.outcome` | Duration of configured gRPC server calls |
| `trogon.eventstore.gossip.exchange.duration` | Histogram | `s` | `trogon.eventstore.activity.name`, `trogon.eventstore.activity.outcome` | Duration of gossip exchanges |
| `trogon.eventstore.gossip.message.processing.duration` | Histogram | `s` | `trogon.eventstore.activity.name`, `trogon.eventstore.activity.outcome` | Duration of gossip message processing |
| `trogon.eventstore.cluster.election.count` | Counter | `{election}` | None | Number of cluster elections |
| `trogon.eventstore.grpc.server.call.active` | UpDownCounter | `{call}` | None | Number of active incoming gRPC calls |
| `trogon.eventstore.grpc.server.call.count` | Counter | `{call}` | None | Number of incoming gRPC calls |
| `trogon.eventstore.grpc.server.call.failure.count` | Counter | `{call}` | None | Number of failed incoming gRPC calls |
| `trogon.eventstore.grpc.server.call.unimplemented.count` | Counter | `{call}` | None | Number of unimplemented incoming gRPC calls |
| `trogon.eventstore.grpc.server.call.deadline_exceeded.count` | Counter | `{call}` | None | Number of incoming gRPC calls that exceeded their deadline |
| `trogon.eventstore.system.load_average` | Gauge | `1` | `trogon.eventstore.system.load_average.period` | System load average for period `1m`, `5m`, or `15m`; not emitted on Windows |
| `trogon.eventstore.process.disk.operation.count` | Counter | `{operation}` | `disk.io.direction` | Number of process disk operations; direction is `read` or `write` |

The failure, unimplemented, and deadline-exceeded counters mirror distinct diagnostic events. They are separate instruments because the source events can overlap for one call and therefore must not be summed as mutually exclusive outcomes.

### Queues

| Instrument | Kind | Unit | Attributes | Description |
| --- | --- | --- | --- | --- |
| `trogon.eventstore.queue.message.wait.duration.max` | Gauge | `s` | `trogon.eventstore.queue.name`, `trogon.eventstore.measurement.window` | Maximum queue wait during the measurement window |
| `trogon.eventstore.queue.message.processing.duration` | Histogram | `s` | `trogon.eventstore.queue.name`, `trogon.eventstore.queue.message.type` | Queue message processing duration |
| `trogon.eventstore.queue.busy.time` | Counter | `s` | `trogon.eventstore.queue.name` | Total time a queue has spent processing messages |
| `trogon.eventstore.queue.message.count` | UpDownCounter | `{message}` | `trogon.eventstore.queue.name` | Messages waiting in a queue |

Queue and message labels come from the `QueueLabels` and `MessageTypes` regular-expression mappings in `metricsconfig.json`. Processing histograms have the highest cardinality and collection cost because they combine queue and message-type labels.

### Storage and caches

| Instrument | Kind | Unit | Attributes | Description |
| --- | --- | --- | --- | --- |
| `trogon.eventstore.storage.io` | Counter | `By` | `trogon.eventstore.storage.activity` | Bytes transferred by storage operations |
| `trogon.eventstore.storage.event.count` | Counter | `{event}` | `trogon.eventstore.storage.activity` | Events processed by storage operations |
| `trogon.eventstore.storage.record.read.duration` | Histogram | `s` | `trogon.eventstore.storage.source` | Transaction file record read duration |
| `trogon.eventstore.storage.chunk.read.distance` | Histogram | `{chunk}` | None | Logical chunk distance between a read and the writer position |
| `trogon.eventstore.cache.operation.count` | Counter | `{operation}` | `trogon.eventstore.cache.name`, `trogon.eventstore.cache.result` | Cache lookups by result |
| `trogon.eventstore.cache.resource.size` | UpDownCounter | `By` | `trogon.eventstore.cache.name`, `trogon.eventstore.cache.resource` | Cache size and capacity in bytes |
| `trogon.eventstore.cache.resource.count` | UpDownCounter | `{entry}` | `trogon.eventstore.cache.name`, `trogon.eventstore.cache.resource` | Cache size, capacity, and count in entries |
| `trogon.eventstore.checkpoint.position` | Gauge | `1` | `trogon.eventstore.checkpoint.name`, `trogon.eventstore.checkpoint.read_kind` | Transaction log checkpoint position |
| `trogon.eventstore.writer.flush.size.max` | Gauge | `By` | `trogon.eventstore.measurement.window` | Maximum writer flush size during the measurement window |
| `trogon.eventstore.writer.flush.duration.max` | Gauge | `s` | `trogon.eventstore.measurement.window` | Maximum writer flush duration during the measurement window |

The `trogon.eventstore.storage.activity` attribute is `read` or `write`. The checkpoint read kind currently emitted by the node is `non_flushed`.

### Persistent subscriptions

Every persistent subscription instrument includes `trogon.eventstore.persistent_subscription.stream` and `trogon.eventstore.persistent_subscription.group`.

| Instrument | Kind | Unit | Additional attributes | Description |
| --- | --- | --- | --- | --- |
| `trogon.eventstore.persistent_subscription.connection.count` | UpDownCounter | `{connection}` | None | Subscription connections |
| `trogon.eventstore.persistent_subscription.parked_message.count` | UpDownCounter | `{message}` | None | Parked messages |
| `trogon.eventstore.persistent_subscription.in_flight_message.count` | UpDownCounter | `{message}` | None | In-flight messages |
| `trogon.eventstore.persistent_subscription.oldest_parked_message.age` | Gauge | `s` | None | Age of the oldest parked message |
| `trogon.eventstore.persistent_subscription.park_request.count` | Counter | `{request}` | `trogon.eventstore.persistent_subscription.reason` | Park requests by reason |
| `trogon.eventstore.persistent_subscription.parked_message.replay.count` | Counter | `{operation}` | None | Parked-message replay operations |
| `trogon.eventstore.persistent_subscription.parked_message.truncate.count` | Counter | `{operation}` | None | Parked-message truncate operations |
| `trogon.eventstore.persistent_subscription.item.processed.count` | Counter | `{item}` | None | Processed subscription items |
| `trogon.eventstore.persistent_subscription.last_known.event.revision` | Gauge | `1` | None | Last known stream revision |
| `trogon.eventstore.persistent_subscription.last_known.commit.position` | Gauge | `1` | None | Last known commit position |
| `trogon.eventstore.persistent_subscription.checkpoint.event.revision` | Gauge | `1` | None | Last checkpointed stream revision |
| `trogon.eventstore.persistent_subscription.checkpoint.commit.position` | Gauge | `1` | None | Last checkpointed commit position |

### Projections

| Instrument | Kind | Unit | Attributes | Description |
| --- | --- | --- | --- | --- |
| `trogon.eventstore.projection.event.processed.count` | Counter | `{event}` | `trogon.eventstore.projection.name` | Events processed by a projection after restart |
| `trogon.eventstore.projection.progress` | Gauge | `1` | `trogon.eventstore.projection.name` | Completion fraction clamped to the range from `0` to `1` |
| `trogon.eventstore.projection.status` | UpDownCounter | `1` | `trogon.eventstore.projection.name`, `trogon.eventstore.projection.status` | One-hot projection state using `running`, `faulted`, and `stopped` series |
| `trogon.eventstore.projection.state.size` | UpDownCounter | `By` | `trogon.eventstore.projection.name` | Total projection state size across partitions |

There is no separate projection-running instrument. Use `trogon.eventstore.projection.status` with `trogon.eventstore.projection.status="running"`.

## Configuration

`ExpectedScrapeIntervalSeconds` must be `1`, `5`, `10`, or a multiple of `15`. A value of `0` disables node-specific Core metric collection. The value also defines the window used by recent-maximum instruments.

This example uses only current configuration keys:

```json
{
  "ExpectedScrapeIntervalSeconds": 15,
  "Otlp": {
    "Enabled": true
  },
  "Meters": [],
  "System": {
    "LoadAverage1m": true,
    "LoadAverage5m": true,
    "LoadAverage15m": true,
    "DriveTotalBytes": true,
    "DriveUsedBytes": true
  },
  "Process": {
    "UpTime": true,
    "DiskReadBytes": true,
    "DiskReadOps": true,
    "DiskWrittenBytes": true,
    "DiskWrittenOps": true
  },
  "ProjectionStats": true,
  "PersistentSubscriptionStats": true
}
```

OTLP exporter endpoint, protocol, headers, and export interval settings use the standard OpenTelemetry .NET `OTEL_*` environment variables.

The remaining node-specific switches map to these instrument groups:

| Configuration key | Instruments controlled |
| --- | --- |
| `Statuses` | Component status |
| `ElectionsCount` | Cluster election count |
| `Checkpoints` | Checkpoint position |
| `Events` | Storage I/O, event count, record read duration, and chunk read distance |
| `GrpcMethods` | Configured gRPC method duration histograms |
| `IncomingGrpcCalls` | Active, total, failure, unimplemented, and deadline-exceeded incoming gRPC calls |
| `Gossip` | Gossip exchange and processing durations |
| `Writer` | Writer flush maximum size and duration |
| `CacheHitsMisses` | Cache operation count |
| `CacheResources` | Cache resource size and count |
| `System` | Load average and filesystem usage or limit |
| `Process` | Process uptime, disk bytes, and disk operation count |
| `Queues` | Queue busy time, length, wait maximum, and processing duration |
| `ProjectionStats` | Projection instruments |
| `PersistentSubscriptionStats` | Persistent subscription instruments |

Native `System.Runtime` and Kestrel instruments are not controlled by node-specific metric switches. Use OpenTelemetry views or exporter configuration when filtering those sources.
