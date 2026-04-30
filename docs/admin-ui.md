---
title: Admin UI
---

# Admin UI

The EventStoreDB Admin UI is available at `http://SERVER_IP:2113/ui` and helps operators inspect and manage a node or cluster from the browser.

## Dashboard

The dashboard opens at `/ui` and combines the daily operational view in one place:

- _Cluster status_: live gossip membership, node state, checkpoints, TCP and HTTP endpoints, replica status, and a copy-friendly snapshot.
- _Queue pressure_: live queue length, throughput, processing time, and currently processed messages.
- _Node probes_: inline Ping, Node info, and Gossip checks rendered inside the UI.

## Navigator

The _Navigator_ page links to the main workspace areas: streams, queries, projections, subscriptions, users, operations, observability, and configuration.

## Observability

The _Observability_ page focuses on runtime diagnostics:

- queue groups and individual queue rows
- current and last processed messages
- TCP connection statistics
- snapshot output for copy-paste debugging

## Configuration

The _Configuration_ page shows node version information, loaded configuration options, and optional subsystem status.

## Streams

The _Streams_ page lets you find recently created or changed streams and open a stream by name. Stream detail pages provide:

- paged event browsing
- event detail views with data and metadata
- append, metadata, ACL, delete, and query actions

## Query

The _Query_ page runs transient JavaScript projections for short-lived analysis and can stop the generated transient projection from the same page.

## Projections

The _Projections_ page shows user, system, and optionally transient projections. It supports creating projections, creating standard projections, bulk enable or disable, and opening a projection for inspection.

Projection detail pages provide source, status, config, state, result, debug, edit, reset, and delete workflows. Projection features are available when projections are enabled on the node.

## Subscriptions

The _Subscriptions_ page lists persistent subscriptions by stream and group. Detail pages show connection state, lag, settings, parked messages, and replay/edit/delete actions.

## Users

The _Users_ page shows the active request identity and user accounts. It supports creating users, editing user details, resetting passwords, and enabling, disabling, or deleting accounts.

## Operations

The _Operations_ page provides privileged node actions:

- start and inspect scavenges
- stop active scavenges
- merge indexes
- set node priority
- reload trusted certificates
- request node shutdown
- inspect optional subsystem status

## Sign in and sign out

The UI supports the node authentication providers configured for the server. Use _Sign out_ to clear the UI credentials for the current browser session.
