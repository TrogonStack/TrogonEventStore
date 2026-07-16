---
title: "Installation"
---

# Installation

TrogonEventStore can run as a single node for local development or as a cluster
for production-like deployments.

## Default access

| User  | Password |
|-------|----------|
| admin | changeit |
| ops   | changeit |

Change the default credentials before using a node outside a disposable local
environment.

## Local development

Build the cluster node from source:

```bash:no-line-numbers
dotnet build -c Release src
```

Start a single local node:

```bash:no-line-numbers
dotnet ./src/EventStore.ClusterNode/bin/Release/net10.0/EventStore.ClusterNode.dll \
  --dev \
  --db ./tmp/data \
  --index ./tmp/index \
  --log ./tmp/log
```

The `--dev` option is intended for disposable local development. For any shared
or production-like environment, configure certificates and authentication
explicitly.

## Docker

Build a local Docker image from the repository:

```bash:no-line-numbers
docker build --tag trogondb-local . \
  --build-arg CONTAINER_RUNTIME=noble \
  --build-arg RUNTIME=linux-x64
```

Run a single local node:

```bash:no-line-numbers
docker run --rm --name trogondb-node -it -p 2113:2113 \
  trogondb-local \
  --dev \
  --db /var/lib/trogondb/data \
  --index /var/lib/trogondb/index \
  --log /var/log/trogondb
```

For durable local data, mount database, index, and log directories into the
container.

## Production checklist

Before running a durable node or cluster:

- Provide node certificates explicitly.
- Decide whether clients use TLS and configure the connection strings
  accordingly.
- Configure authentication methods in [Security](security.md).
- Store data, index, and logs on durable volumes.
- Expose `/-/liveness`, `/-/readiness`, and `/-/metrics` to the platform.
- Use gRPC clients for application reads and writes.

## Linux service notes

When running on Linux, set the open file limit high enough for the expected
database size and workload. The precise value depends on the deployment, but
operators commonly start between `30000` and `60000`.

Configuration can be supplied with command-line options, YAML, or environment
variables. See [Configuration](configuration.md).

## Windows service notes

TrogonEventStore can run under the Windows Service Control Manager, but the
source build does not register itself automatically.

Example service registration:

```powershell:no-line-numbers
sc.exe create TrogonDB binPath= "C:\TrogonDB\EventStore.ClusterNode.exe --config C:\TrogonDB\trogondb.conf"
sc.exe start TrogonDB
```

If the HTTP listener needs a URL ACL, configure it explicitly:

```powershell:no-line-numbers
netsh http add urlacl url=http://+:2113/ user=DOMAIN\username
```

For more information, refer to Microsoft's `add urlacl` documentation.

## Cluster startup

A production cluster normally uses three nodes. For each node:

1. Create a node-specific configuration file.
2. Provide node certificates and trusted roots.
3. Configure gossip addresses for the cluster members.
4. Start the node.
5. Check readiness on `/-/readiness`.
6. Check the cluster view in the Admin UI.
