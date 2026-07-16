# Introduction

Welcome to the TrogonEventStore documentation.

TrogonEventStore is the open-source event database behind TrogonDB. It stores
business events durably, streams them to consumers, and exposes operational
surfaces for running a node or cluster.

## Current product direction

TrogonEventStore keeps the database node focused on the durable event log:

- Application event access is gRPC-first.
- HTTP is reserved for the Admin UI, health probes, metrics, and other
  infrastructure-level concerns.
- The project is FOSS-only. The documentation does not describe unsupported
  proprietary server features.
- Rich read models, user-defined query engines, connector runtimes, and
  projection execution are expected to live outside the database node unless a
  local product decision says otherwise.

Read the [architecture direction](architecture.md) before adding new runtime
surfaces to the core node.

## Getting started

Use the [installation guide](installation.md) for local development, Docker, and
cluster startup guidance.

For a production node, review:

- [Configuration](configuration.md)
- [Networking](networking.md)
- [Security](security.md)
- [Operations](operations.md)
- [Diagnostics](diagnostics/README.md)

## Protocols and clients

The supported application protocol is gRPC. Existing TrogonEventStore-compatible
gRPC clients can be useful while the TrogonDB client libraries continue to
evolve, but the server documentation should be treated as authoritative for this
repository.

HTTP endpoints are not an application event API. They are used for browser UI,
health, metrics, and infrastructure integration.

## Admin UI

The embedded Admin UI is served from the node and is documented in
[Admin UI](admin-ui.md). It is browser plumbing for operators, not a replacement
for the gRPC application API.

## Observability

Use [metrics](diagnostics/metrics.md), [logs](diagnostics/logs.md), and
[OpenTelemetry integration](diagnostics/integrations.md) for production
observability.

The HTTP probe endpoints are:

- `/-/liveness`
- `/-/readiness`
- `/-/metrics`

## Support and issues

Use the TrogonStack repository and community channels for issues, discussions,
and feature requests for this distribution.
