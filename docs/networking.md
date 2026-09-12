---
title: Networking
---

## Network configuration

TrogonEventStore provides two HTTP(S) interfaces:

- The node endpoint carries database client APIs, cluster coordination, follower-to-leader request forwarding, the Admin UI, health probes, metrics, and other operator endpoints.
- The replication endpoint carries node-to-node database replication over gRPC.

The node does not open a separate legacy EventStore TCP protocol listener or
accept the legacy TCP client protocol. gRPC traffic on both interfaces uses
HTTP/2 over TCP at the operating-system transport layer. Keep the replication
endpoint on the private cluster network and expose the node endpoint according
to client and operator requirements.

## HTTP configuration

HTTP/2 on the node endpoint carries gRPC traffic for database clients and
internal cluster coordination, while regular HTTP routes serve the Admin UI,
health checks, metrics, and supported diagnostics.
The HTTP endpoint always binds to the IP address configured in the `NodeIp` setting (previously referred to as `ExtIp`).

| Format               | Syntax               |
|:---------------------|:---------------------|
| Command line         | `--node-ip`          |
| YAML                 | `NodeIp`             |
| Environment variable | `EVENTSTORE_NODE_IP` |

`NodeIp` defaults to `127.0.0.1`. Set it to `0.0.0.0` to bind all interfaces and configure a
`NodeHostAdvertiseAs` value that clients can resolve, since `0.0.0.0` is not a connectable address.

::: warning
Please note that the `ExtIp` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `NodeIp` parameter instead.
:::

The default HTTP port is `2113`. Depending on the [security settings](security.md) of the node, it either responds over plain HTTP or via HTTPS. There is no HSTS redirect, so if you try reaching a secure node via HTTP, you can get an empty response.

You can change the HTTP port using the `NodePort` setting (previously `HttpPort` setting) :

| Format               | Syntax                 |
|:---------------------|:-----------------------|
| Command line         | `--node-port`          |
| YAML                 | `NodePort`             |
| Environment variable | `EVENTSTORE_NODE_PORT` |

**Default**: `2113`

::: warning
Please note that the `HttpPort` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `NodePort` parameter instead.
:::

If your network setup requires any kind of IP address, DNS name and port translation for internal or external communication, you can use available [address translation](#network-address-translation) settings.

### Keep-alive pings

The reliability of the connection between the client application and database is crucial for the stability of the solution. If the network is not stable or has some periodic issues, the client may drop the connection. Stability is essential for stream subscriptions where a client is listening to database notifications. Having an existing connection open when an app resumes activity allows for the initial gRPC calls to be made quickly, without any delay caused by the reestablished connection.

TrogonEventStore supports the built-in gRPC mechanism for keeping the connection alive. If the other side does not acknowledge the ping within a certain period, the connection will be closed. Note that pings are only necessary when there's no activity on the connection.

Keepalive pings are enabled by default, with the default interval set to 10 seconds. The default value is based on the [gRPC proposal](https://github.com/grpc/proposal/blob/master/A8-client-side-keepalive.md#extending-for-basic-health-checking) that suggests 10 seconds as the minimum. It's a compromise value to ensure that the connection is open and not making too many redundant network calls.

You can customise the following Keepalive settings:

#### KeepAliveInterval

After a duration of `keepAliveInterval` (in milliseconds), if the server doesn't see any activity, it pings the client to see if the transport is still alive.

| Format               | Syntax                           |
|:---------------------|:---------------------------------|
| Command line         | `--keep-alive-interval`          |
| YAML                 | `KeepAliveInterval`              |
| Environment variable | `EVENTSTORE_KEEP_ALIVE_INTERVAL` |

**Default**: `10000` (ms, 10 sec)

#### KeepAliveTimeout

After having pinged for keepalive check, the server waits for a duration of `keepAliveTimeout` (in milliseconds). If the connection doesn't have any activity even after that, it gets closed.

| Format               | Syntax                          |
|:---------------------|:--------------------------------|
| Command line         | `--keep-alive-timeout`          |
| YAML                 | `KeepAliveTimeout`              |
| Environment variable | `EVENTSTORE_KEEP_ALIVE_TIMEOUT` |

**Default**: `10000` (ms, 10 sec)

As a general rule, we do not recommend putting TrogonEventStore behind a load balancer. However, if you are using it and want to benefit from the Keepalive feature, then you should make sure if the compatible settings are properly set. Some load balancers may also override the Keepalive settings. Most of them require setting the idle timeout larger/longer than the `keepAliveTimeout`. We suggest checking the load balancer documentation before using Keepalive pings.

### HTTP caching

:::tip
This section is about caching HTTP resources such as the Admin UI. It does not affect the server performance directly and cannot be used with gRPC clients.
:::

Most static resources that TrogonEventStore emits are immutable and can be cached safely.

This caching behavior is great for performance in a production environment and we recommended you use it, but in a developer environment it can become confusing.

To avoid this during development it's best to run TrogonEventStore with the `--disable-http-caching` command line option. This disables all caching and solves the issue.

The option can be set as follows:

| Format               | Syntax                            |
|:---------------------|:----------------------------------|
| Command line         | `--disable-http-caching`          |
| YAML                 | `DisableHttpCaching`              |
| Environment variable | `EVENTSTORE_DISABLE_HTTP_CACHING` |

**Default**: `false`, so the HTTP caching is **enabled** by default.

### Kestrel Settings

It's generally not expected that you'll need to update the Kestrel configuration that TrogonEventStore has set by default, but it's good to know that you can update the following settings if needed.

Kestrel uses the `kestrelsettings.json` configuration file. This file should be located in the [default configuration directory](configuration.md#configuration-file).

#### MaxConcurrentConnections

Sets the maximum number of open connections. See the docs [here](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxconcurrentconnections?view=aspnetcore-5.0).

This is configured with `Kestrel.Limits.MaxConcurrentConnections` in the settings file.

#### MaxConcurrentUpgradedConnections

Sets the maximum number of open, upgraded connections. An upgraded connection is one that has been switched from HTTP to another protocol, such as WebSockets. See the docs [here](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxconcurrentupgradedconnections?view=aspnetcore-5.0).

This is configured with `Kestrel.Limits.MaxConcurrentUpgradedConnections` in the settings file.

#### Http2 InitialConnectionWindowSize

Sets how much request body data the server is willing to receive and buffer at a time aggregated across all requests (streams) per connection. Note requests are also limited by `KestrelInitialStreamWindowSize`

The value must be greater than or equal to 65,535 and less than 2^31. See the docs [here](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.http2limits.initialconnectionwindowsize?view=aspnetcore-5.0).

This is configured with `Kestrel.Limits.Http2.InitialConnectionWindowSize` in the settings file.

#### Http2 InitialStreamWindowSize

Sets how much request body data the server is willing to receive and buffer at a time per stream. Note connections are also limited by `KestrelInitialConnectionWindowSize`

Value must be greater than or equal to 65,535 and less than 2^31. See the docs [here](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.http2limits.initialstreamwindowsize?view=aspnetcore-5.0).

This is configured with `Kestrel.Limits.Http2.InitialStreamWindowSize` in the settings file.

## Internal cluster traffic

Cluster replication uses a dedicated HTTP/2 listener so it can remain isolated
from client and operator traffic while using gRPC. Follower-to-leader request
forwarding and cluster coordination continue to use the node endpoint.

The replication listener binds to `ReplicationIp`:

| Format               | Syntax                      |
|:---------------------|:----------------------------|
| Command line         | `--replication-ip`          |
| YAML                 | `ReplicationIp`             |
| Environment variable | `EVENTSTORE_REPLICATION_IP` |

**Default**: `127.0.0.1` (loopback).

For a multi-node cluster, bind this setting to an interface reachable by the
other database nodes. `0.0.0.0` binds all IPv4 interfaces.

The replication listener uses `ReplicationPort`:

| Format               | Syntax                        |
|:---------------------|:------------------------------|
| Command line         | `--replication-port`          |
| YAML                 | `ReplicationPort`             |
| Environment variable | `EVENTSTORE_REPLICATION_PORT` |

**Default**: `1112`

When TLS is enabled, internal gRPC connections use the configured node
certificate. `DisableTls` disables encryption for both HTTP(S) listeners while
preserving authentication and authorization.

Replication connections use HTTP/2 keepalive pings for failure detection. The
existing replication heartbeat settings configure the client-side ping interval
and acknowledgement timeout:

| Format               | Interval                                   | Timeout                                   |
|:---------------------|:-------------------------------------------|:------------------------------------------|
| Command line         | `--replication-heartbeat-interval`         | `--replication-heartbeat-timeout`         |
| YAML                 | `ReplicationHeartbeatInterval`             | `ReplicationHeartbeatTimeout`             |
| Environment variable | `EVENTSTORE_REPLICATION_HEARTBEAT_INTERVAL` | `EVENTSTORE_REPLICATION_HEARTBEAT_TIMEOUT` |

**Default**: `700` ms for both settings.

Values below `1000` ms remain valid for configuration compatibility and use the
HTTP/2 transport minimum of `1000` ms.

## Network address translation

Due to NAT (network address translation), or other reasons a node may not be bound to the address it is reachable from other nodes. For example, the machine has an IP address of `192.168.1.13`, but the node is visible to other nodes as `10.114.12.112`.

Options described below allow you to tell the node that even though it is bound to a given address it should not gossip that address. When returning links over HTTP, TrogonEventStore will also use the specified addresses instead of physical addresses, so the clients that use HTTP can follow those links.

Another case when you might want to specify the advertised address although there's no address translation involved. When you configure TrogonEventStore to bind to `0.0.0.0`, it will use the first non-loopback address for gossip. It might or might not be the address you want it to use. Configure `NodeHostAdvertiseAs` for the node endpoint and `ReplicationHostAdvertiseAs` for the replication endpoint when other nodes must connect using specific IP addresses or hostnames.

You might also override the advertised address when secure cluster certificates
contain DNS names rather than IP addresses.

The only place where these settings make any effect is the [gossip](cluster.md#gossip-protocol) endpoint response.

## HTTP(S) endpoint advertisement

By default, a cluster node will advertise itself using `NodeIp` and `NodePort`. You can override the advertised HTTP port using the  `NodePortAdvertiseAs` setting (previously `HttpPortAdvertiseAs` setting).

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Command line         | `--node-port-advertise-as`          |
| YAML                 | `NodePortAdvertiseAs`               |
| Environment variable | `EVENTSTORE_NODE_PORT_ADVERTISE_AS` |

::: warning
Please note that the `HttpPortAdvertiseAs` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `NodePortAdvertiseAs` parameter instead.
:::

If you want the node to advertise itself using the hostname rather than its IP address, use the `NodeHostAdvertiseAs` setting (previously `ExtHostAdvertiseAs` setting).

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Command line         | `--node-host-advertise-as`          |
| YAML                 | `NodeHostAdvertiseAs`               |
| Environment variable | `EVENTSTORE_NODE_HOST_ADVERTISE_AS` |

::: warning
Please note that the `ExtHostAdvertiseAs` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `NodeHostAdvertiseAs` parameter instead.
:::

## Replication endpoint advertisement

If the bound replication address or port is not reachable as-is from the other
nodes, override the endpoint advertised through internal gossip.

| Format               | Host syntax                                  | Port syntax                                      |
|:---------------------|:---------------------------------------------|:-------------------------------------------------|
| Command line         | `--replication-host-advertise-as`            | `--replication-tcp-port-advertise-as`            |
| YAML                 | `ReplicationHostAdvertiseAs`                 | `ReplicationTcpPortAdvertiseAs`                  |
| Environment variable | `EVENTSTORE_REPLICATION_HOST_ADVERTISE_AS`   | `EVENTSTORE_REPLICATION_TCP_PORT_ADVERTISE_AS`   |

The port option retains its existing name for configuration compatibility, but
the advertised endpoint now carries gRPC replication rather than the removed
TCP replication protocol.

### Advertise to clients

In some cases, the cluster needs to advertise itself to clients using a completely different set of addresses and ports. Usually, you need to do it because addresses and ports configured for the HTTP protocol are not available as-is to the outside world. One of the examples is running a cluster in Docker Compose. In such environment, HTTP uses internal hostnames in the Docker network, which isn't accessible on the host. So, in order to connect to the cluster from the host machine, you need to use `localhost` and translated HTTP ports to reach the cluster nodes.

To configure how the cluster nodes advertise to clients, use the `Advertise<*>ToClient` settings listed below.

Specify the advertised hostname or IP address:

| Format               | Syntax                                   |
|:---------------------|:-----------------------------------------|
| Command line         | `--advertise-host-to-client-as`          |
| YAML                 | `AdvertiseHostToClientAs`                |
| Environment variable | `EVENTSTORE_ADVERTISE_HOST_TO_CLIENT_AS` |

Specify the advertised HTTP(S) port (previously `AdvertiseHttpPortToClientAs` setting):

| Format               | Syntax                                        |
|:---------------------|:----------------------------------------------|
| Command line         | `--advertise-node-port-to-client-as`          |
| YAML                 | `AdvertiseNodePortToClientAs`                 |
| Environment variable | `EVENTSTORE_ADVERTISE_NODE_PORT_TO_CLIENT_AS` |

::: warning
Please note that the `AdvertiseHttpPortToClientAs` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `AdvertiseNodePortToClientAs` parameter instead.
:::

## Exposing endpoints

If you need to reduce the HTTP surface, you can disable the browser-facing Admin UI and the Prometheus metrics endpoint. Health probes and gRPC remain part of the supported HTTP listener.

You can disable the Admin UI and its administrative API endpoints by setting `DisableAdminUi` to `true`.

| Format               | Syntax                    |
|:---------------------|:--------------------------|
| Command line         | `--disable-admin-ui`      |
| YAML                 | `DisableAdminUi`          |
| Environment variable | `EVENTSTORE_DISABLE_ADMIN_UI` |

**Default**: `false`, Admin UI and administrative API endpoints are enabled.

You can disable the Prometheus metrics endpoint by setting `DisableStatsOnHttp` to `true`.

| Format               | Syntax                              |
|:---------------------|:------------------------------------|
| Command line         | `--disable-stats-on-http`           |
| YAML                 | `DisableStatsOnHttp`                |
| Environment variable | `EVENTSTORE_DISABLE_STATS_ON_HTTP`  |

**Default**: `false`, the Prometheus metrics endpoint is enabled on `/-/metrics`.

## Protocol boundary

Database client APIs, replication, and follower-to-leader forwarding use gRPC.
The remaining HTTP routes are operator surfaces, not an application event API.
The server has no legacy EventStore TCP protocol listener, TCP client protocol,
or TCP replication protocol. The separately configurable replication listener
is HTTP/2-only and accepts only the replication gRPC service.
