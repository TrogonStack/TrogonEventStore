---
title: Architecture direction
---

# Architecture direction

TrogonEventStore keeps the database node focused on the durable event log and
the operational work required to keep that log correct, available, and
observable.

The core node owns:

- Appending events to streams
- Reading events from streams and `$all`
- Replication and cluster membership
- Storage, scavenging, and archive recovery
- Authentication, authorization, diagnostics, and health
- Native indexes only when they are part of core database read semantics

This boundary keeps stream ownership simple and leaves room for future storage
and sharding work. Features that need their own compute model, query model, or
serving model should be separate components that consume the database through
subscriptions or reads.

## Projection execution

Projection execution is future external component work by default.

User-defined projection runtimes, rich read models, custom query models, and
parallel projection engines should run outside the database process unless they
become an explicitly accepted native database capability.

This keeps the node from coupling append/read correctness to user code,
scripting runtimes, read-model storage, or projection-specific checkpoint
semantics. External projection components can scale independently, own their
checkpoint and read-model storage, and fail without taking the database node
with them.

## System projections

Built-in system projections are different from a general projection execution
platform.

System projections such as `$streams`, `$by_category`, `$by_event_type`,
`$by_correlation_id`, and `$stream_by_category` exist for compatibility and
query convenience. They may remain as optional in-node behavior while clients
still depend on those streams.

New work should avoid expanding system projections into a broader in-node query
or read-model platform. If a capability can be implemented as an external
projection or subscription component, prefer the external boundary.

## Multi-stream append

Multi-stream append is not required for the current system projection model.

Any future design that needs atomic writes across unrelated streams must be
reviewed as a core database semantics decision, not as projection plumbing. That
decision has consequences for stream independence, storage layout, and future
sharding.

Projection engines that need atomic checkpoint, state, and emitted-event writes
should prefer external checkpointing or component-owned storage unless the core
database explicitly accepts cross-stream write semantics.
