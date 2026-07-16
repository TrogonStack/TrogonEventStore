---
title: Networking
---

## Network configuration

TrogonEventStore provides two interfaces:
- HTTP(S) for gRPC communication, the Admin UI, and operational endpoints such as health checks and metrics
- TCP for cluster replication (internal)

Nodes in the cluster replicate with each other using the TCP protocol, but use gRPC for [discovering other cluster nodes](cluster.md#discovering-cluster-members).

Server nodes use a single HTTP binding for gRPC, the Admin UI, health, metrics,
and supported diagnostics. Replication between cluster nodes is internal and can
be placed on a private network interface. Keep public client access on gRPC and
avoid exposing replication ports outside the cluster network.

For gRPC and HTTP, there's no internal vs external separation of traffic.

## HTTP configuration

HTTP is the primary protocol for TrogonEventStore. It carries gRPC communication, the Admin UI, health checks, metrics, and supported diagnostics endpoints.
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

## Replication protocol

Replication between cluster nodes uses an internal TCP-based protocol. Options for configuring the internal replication protocol are described below.

### Interface and port

Internal TCP binds to the IP address specified in the `ReplicationIp` setting (previously `IntIp`). It must be configured if you run a multi-node cluster.

By default, TrogonEventStore binds its internal networking on the loopback interface only (`127.0.0.1`). You can change this behaviour and tell TrogonEventStore to listen on a specific internal IP address. To do that set the `ReplicationIp` to `0.0.0.0` or the IP address of the network interface.

| Format               | Syntax                      |
|:---------------------|:----------------------------|
| Command line         | `--replication-ip`          |
| YAML                 | `ReplicationIp`             |
| Environment variable | `EVENTSTORE_REPLICATION_IP` |

**Default**: `127.0.0.1` (loopback).

If you keep this setting to its default value, cluster nodes won't be able to talk to each other.

::: warning
Please note that the `IntIp` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `ReplicationIp` parameter instead.
:::

By default, TrogonEventStore uses port `1112` for internal TCP. You can change this by specifying the `ReplicationPort` setting (previously `IntTcpPort` setting).

| Format               | Syntax                        |
|:---------------------|:------------------------------|
| Command line         | `--replication-port`          |
| YAML                 | `ReplicationPort`             |
| Environment variable | `EVENTSTORE_REPLICATION_PORT` |

**Default**: `1112`

::: warning
Please note that the `IntTcpPort` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `ReplicationPort` parameter instead.
:::

### Security

When TLS is enabled, replication uses the configured node certificate. Replication TLS cannot be disabled
independently. `DisableTls` disables TLS for both HTTP and replication while preserving authentication and
authorization.

If your network setup requires any kind of IP address, DNS name and port translation for internal communication, you can use available [address translation](#network-address-translation) settings.

## Network address translation

Due to NAT (network address translation), or other reasons a node may not be bound to the address it is reachable from other nodes. For example, the machine has an IP address of `192.168.1.13`, but the node is visible to other nodes as `10.114.12.112`.

Options described below allow you to tell the node that even though it is bound to a given address it should not gossip that address. When returning links over HTTP, TrogonEventStore will also use the specified addresses instead of physical addresses, so the clients that use HTTP can follow those links.

Another case when you might want to specify the advertised address although there's no address translation involved. When you configure TrogonEventStore to bind to `0.0.0.0`, it will use the first non-loopback address for gossip. It might or might not be the address you want it to use. Whilst the best way to avoid such a situation is to configure the binding properly using the `NodeIp` and `ReplicationIp` settings, you can also use address translation setting with the correct IP address or DNS name.

Also, even if you specified the `NodeIp` and `ReplicationIp` settings in the configuration, you might still want to override the advertised address if you want to use hostnames and not IP addresses. That might be needed when running a secure cluster with certificates that only contain DNS names of the nodes.

The only place where these settings make any effect is the [gossip](cluster.md#gossip-protocol) endpoint response.

## HTTP translations

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

### TCP translations

TCP ports used for replication can be advertised using custom values:

| Format               | Syntax                                         |
|:---------------------|:-----------------------------------------------|
| Command line         | `--replication-tcp-port-advertise-as`          |
| YAML                 | `ReplicationTcpPortAdvertiseAs`                |
| Environment variable | `EVENTSTORE_REPLICATION_TCP_PORT_ADVERTISE_AS` |

::: warning
Please note that the `IntTcpPortAdvertiseAs` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `ReplicationTcpPortAdvertiseAs` and `NodeTcpPortAdvertiseAs` parameters instead, respectively.
:::

If you want to change how the node TCP address is advertised internally, use the `ReplicationHostAdvertiseAs` setting (previously `IntHostAdvertiseAs` setting). You can use an IP address or a hostname.

| Format               | Syntax                                     |
|:---------------------|:-------------------------------------------|
| Command line         | `--replication-host-advertise-as`          |
| YAML                 | `ReplicationHostAdvertiseAs`               |
| Environment variable | `EVENTSTORE_REPLICATION_HOST_ADVERTISE_AS` |

::: warning
Please note that the `IntHostAdvertiseAs` parameter has been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `ReplicationHostAdvertiseAs` parameter instead.
:::

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

## Heartbeat timeouts

TrogonEventStore uses heartbeats over all TCP connections to discover dead clients and nodes. Heartbeat timeouts should not be too short, as short timeouts will produce false positives. At the same time, setting too long timeouts will prevent discovering dead nodes and clients in time.

Each heartbeat has two points of configuration. The first is the _interval;_ this represents how often the system should consider a heartbeat. TrogonEventStore doesn't send a heartbeat for every interval, but only if it has not heard from a node within the configured interval. In a busy cluster, you may never see any heartbeats.

The second point of configuration is the _timeout_. This determines how long TrogonEventStore server waits for a client or node to respond to a heartbeat request.

Different environments need different values for these settings. The defaults are likely fine on a LAN. If you experience frequent elections in your environment, you can try to increase both interval and timeout, for example:

- An interval of 5000ms.
- A timeout of 1000ms.

::: tip
If in doubt, choose higher numbers. This adds a small period of time to discover a dead client or node and is better than the alternative, which is false positives.
:::

Replication/Internal TCP heartbeat (between cluster nodes):

| Format               | Syntax                                      |
|:---------------------|:--------------------------------------------|
| Command line         | `--replication-heartbeat-interval`          |
| YAML                 | `ReplicationHeartbeatInterval`              |
| Environment variable | `EVENTSTORE_REPLICATION_HEARTBEAT_INTERVAL` |

**Default**: `700` (ms)

| Format               | Syntax                                     |
|:---------------------|:-------------------------------------------|
| Command line         | `--replication-heartbeat-timeout`          |
| YAML                 | `ReplicationHeartbeatTimeout`              |
| Environment variable | `EVENTSTORE_REPLICATION_HEARTBEAT_TIMEOUT` |

**Default**: `700` (ms)

::: warning
Please note that the `IntTcpHeartbeatInterval` and `IntTcpHeartbeatTimeout` parameters have been deprecated as of version 23.10.0 and will be removed in future versions. It is recommended to use the `ReplicationHeartbeatInterval` and `ReplicationHeartbeatTimeout` parameters instead, respectively.
:::

### gRPC heartbeats

For the gRPC heartbeats, TrogonEventStore and its gRPC clients use the protocol feature called _Keepalive ping_. Read more about it on the [HTTP configuration page](#keep-alive-pings).

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

## Application protocol boundary

TrogonEventStore does not document an external TCP client protocol. Application
reads and writes should use gRPC clients.

Internal replication can still use node-to-node transport that is not part of
the public client API. Treat those settings as cluster internals, not as a
client integration surface.
