---
title: "Upgrade Guide"
---

# Upgrade Guide

Use this guide when moving a TrogonEventStore node or cluster to a newer build
from this repository.

## Before upgrading

- Back up the database and index directories.
- Record the current server version, configuration file, container image, and
  command-line arguments.
- Check that clients are using gRPC.
- Remove legacy EventStore TCP client-listener settings. Keep the replication
  endpoint, advertisement, and heartbeat settings because they now configure
  the dedicated gRPC replication listener.
- Review changed configuration keys before restarting a durable node.
- Verify that health probes use `/-/liveness` and `/-/readiness`.
- Verify that metrics scraping uses `/-/metrics`.

## Single-node upgrade

1. Stop the node.
2. Replace the binary or container image.
3. Review configuration against [Configuration](configuration.md).
4. Start the node.
5. Wait for `/-/readiness` to return success.
6. Check logs for truncation, index rebuild, or certificate errors.
7. Confirm the Admin UI and gRPC clients can connect.

## Cluster upgrade

Upgrade one node at a time, starting with a follower or read-only replica.

1. Stop one non-leader node.
2. Replace the binary or container image.
3. Start the node.
4. Wait for the node to rejoin the cluster and become ready.
5. Repeat for the next non-leader node.
6. Upgrade the leader last.

During the rollout:

- Client connections can be interrupted when a node restarts or elections occur.
- Write availability depends on cluster quorum.
- Catch-up work can temporarily increase load on the leader.

## Configuration review

Before upgrading, search for obsolete client and HTTP-management settings. The
current product direction is:

- gRPC for database client APIs.
- gRPC over the dedicated replication HTTP(S) endpoint for database replication.
- gRPC over the node HTTP(S) endpoint for follower-to-leader forwarding and
  cluster coordination.
- HTTP for Admin UI, health, metrics, and infrastructure concerns.
- No legacy EventStore TCP client protocol listener.
- No proprietary plugin configuration.

If a setting is no longer documented, remove it rather than carrying it forward
silently.

## Authentication review

Check [Security](security.md) for the current authentication model. Local
username/password authentication and OAuth methods can coexist as configured
methods. Avoid depending on undocumented authentication plugins.

## Observability review

Use [OpenTelemetry integration](diagnostics/integrations.md) for explicit OTLP
export and [Metrics](diagnostics/metrics.md) for Prometheus scraping.

The Admin UI continues to show active connections after the legacy protocol is
removed. The connection table reports the node and replication HTTP/gRPC listeners, while
the replication table reports the database replication sessions and their byte
and queue statistics. The connection table keeps the live paging and
per-second traffic view while identifying HTTP, gRPC, TLS, and the observed
client. These replace the legacy listener-specific TCP table.

The TestClient keeps the established command names that can preserve their
meaning over the supported APIs. Reads, writes, deletes, subscriptions,
scavenging, data verification, load operations, and sanitization checks now
exercise the gRPC APIs on `NodePort`. `CHKGRPC` replaces the protocol-specific
`CHKTCP` malformed-frame check. Historical write-flood aliases remain available
for script compatibility, but they also send gRPC traffic and do not require a
legacy listener.

The historical `RT` suite is not an application API. It was an in-process
developer harness coupled to the legacy TCP client, direct TCP packages, local
node process control, and the old projections client. It has no equivalent in
this release. Reintroducing those projection and node-failure scenarios requires
a dedicated gRPC and HTTP implementation rather than routing the `RT` name to a
different workload.

`ClientMessageDtos.proto` is not a gRPC application contract. It encoded the
payloads placed inside legacy TCP packages, so it is removed with that wire
protocol. The supported replacements are the service contracts under
`src/Protos/Grpc`, including streams, persistent subscriptions, operations,
monitoring, gossip, replication, and request forwarding.

Legacy usage telemetry is separate from OTLP observability. See
[Usage telemetry](usage-telemetry.md) before running a node in an environment
that should not make outbound telemetry calls.
