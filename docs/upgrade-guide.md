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

- gRPC for application event access.
- HTTP for Admin UI, health, metrics, and infrastructure concerns.
- No external TCP client protocol.
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

Legacy usage telemetry is separate from OTLP observability. See
[Usage telemetry](usage-telemetry.md) before running a node in an environment
that should not make outbound telemetry calls.
