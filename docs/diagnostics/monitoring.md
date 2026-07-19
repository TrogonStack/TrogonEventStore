---
title: "Monitoring and alerting"
---

# Monitoring and alerting

Monitor every node independently and evaluate cluster-wide conditions from the combined signals. The
[metrics reference](metrics.md) defines every metric, while [monitoring integrations](integrations.md) explains
how to collect metrics, logs, and traces with Prometheus or OpenTelemetry.

The HTTP health and metrics endpoints are available without application authentication. Restrict access at the
network or platform boundary when the telemetry must not be exposed outside the monitoring network.

## Start with availability

Use the HTTP health endpoints for platform probes:

- `/-/liveness` reports whether the process is alive. Use it to detect a process that must be restarted.
- `/-/readiness` reports whether the node has completed startup and is not shutting down. Remove an unready node
  from client and load-balancer traffic without treating normal startup or graceful shutdown as a crashed
  process. Readiness does not report leader availability during an election.

Alert when a node remains unready beyond the duration established by normal startup and planned
maintenance. For a cluster, also use `eventstore_statuses{name="Node",status=...}` to verify that the expected
number of voting nodes is available and that exactly one node is the leader. The current status is the status
label on the newest sample for each node. Do not count stale series for statuses the node previously held.

## Alert on symptoms and trends

Begin with workload-specific baselines. Alert on sustained deviations rather than isolated samples, then tune
the evaluation window to remain longer than the configured scrape interval.

### Repeated elections

Alert on an unexpected increase in `eventstore_elections_count`. Frequent leadership changes interrupt traffic
and usually indicate node, network, or resource instability.

### Replication falling behind

Compare `eventstore_checkpoints{name="writer"}` on the leader with the same writer checkpoint on each follower.
A follower whose writer checkpoint does not converge reduces the cluster's recovery margin. A restored node can
lag temporarily, but the difference should trend toward zero.

### Saturated processing queues

Alert on sustained growth in `eventstore_queue_length_items` or
`eventstore_queue_queueing_duration_max_seconds`, and on a high rate of change in the cumulative
`eventstore_queue_busy_seconds` counter. Growing queues show that work arrives faster than the node can process
it. Correlate the affected queue group with CPU, storage latency, and request load.

### gRPC failures or deadlines

Alert on increases in `eventstore_incoming_grpc_calls{kind="failed"}` or
`eventstore_incoming_grpc_calls{kind="deadline-exceeded"}`, and on failed
`eventstore_grpc_method_duration_seconds` observations. Break failures down by operation and correlate them with
elections, queue pressure, and network errors.

### Persistent subscription lag

Alert on a growing difference between the last-known and checkpointed event position for a subscription. A
growing gap means consumers are not keeping up. Also alert on parked messages and the age of the oldest parked
message.

The persistent-subscription position metrics differ by source. Stream subscriptions expose
`eventstore_persistent_sub_last_known_event_number` and
`eventstore_persistent_sub_checkpointed_event_number`. Subscriptions to `$all` expose the corresponding
`eventstore_persistent_sub_last_known_event_commit_position` and
`eventstore_persistent_sub_checkpointed_event_commit_position` metrics.

### Projection failure

Alert when `eventstore_projection_status{status="Faulted"}` is `1`, when a required projection stops
unexpectedly, or when its progress declines. A required system projection that stops advancing can leave its
generated streams stale.

### Resource exhaustion

Alert on low free disk or memory, sustained CPU pressure, growing thread-pool pending work, or long garbage
collection pauses. Resource pressure can increase queue time, trigger timeouts, and make a node miss cluster
heartbeats.

## Keep logs in the same incident view

Metrics show that behavior changed; structured logs usually explain why. Collect logs from every node and
correlate them by timestamp with health changes and metric alerts. Prioritize alerts for:

- repeated election, gossip, replication, or certificate-validation errors;
- a node that cannot open or verify its database or index;
- a projection entering the faulted state;
- persistent-subscription messages parked because retries were exhausted;
- an unexpected process shutdown or startup failure.

Do not alert on every warning in isolation. Establish the warnings expected during maintenance, such as a
rolling certificate update, and alert when they persist beyond that operation's planned window.

## Validate the monitoring path

Before relying on an alert in production:

1. Confirm every node is represented in the telemetry backend.
2. Gracefully stop a test node and verify that readiness fails without a liveness-triggered forced restart.
3. Perform a controlled leader resignation and verify the election, node-role, and client-retry signals.
4. Pause a test subscription consumer and verify that its checkpoint gap grows. Separately, reject test
   messages until they are parked and verify the parked-message signals.
5. Test notification routing and record the dashboard, logs, and runbook needed to investigate each alert.

Repeat these checks after changing metric configuration, scrape intervals, exporters, or alert labels.
