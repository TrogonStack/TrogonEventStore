<!-- markdownlint-disable MD033 MD041 -->
<p align="center">
  <a href="https://www.trogondb.com/">
    <img src="./trogon-logo.png" width="240px" alt="TrogonDB" />
  </a>
</p>
<!-- markdownlint-enable MD033 MD041 -->

# TrogonDB

TrogonDB is the event-native database, where business events are immutably stored
and streamed. Designed for event-sourced, event-driven, and microservices
architectures.

- [What is TrogonDB](#what-is-trogondb)
- [Documentation](#docs)
- [Getting started with TrogonDB](#getting-started-with-trogondb)
- [Client libraries](#client-libraries)
- [Community](#community)
- [Building TrogonDB](#building-trogondb)

## What is TrogonDB

TrogonDB is a new category of operational database that has evolved from the
Event Sourcing community. Powered by the state-transition data model, events are
stored with the context of why they have happened. Providing flexible, real-time
data insights in the language your business understands.

Download the [latest version](https://www.trogondb.com/downloads).
For more product information visit [the website](https://www.trogondb.com/TrogonDB).

## Docs

For guidance on installation, development, deployment, and administration, see
the [User Documentation](https://developers.trogondb.com/).

## Getting started with TrogonDB

Follow the [getting started guide](https://developers.trogondb.com/latest.html).

## Getting started with Event Store Cloud

Event Store can manage TrogonDB for you, so you don't have to run your own
clusters.
See the online documentation:
[Getting started with Event Store Cloud](https://developers.trogondb.com/cloud/).

## Client libraries

This guide shows you how to get started with TrogonDB by setting up an instance
or cluster and configuring it.

TrogonDB supported gRPC clients:

- Python: [pyeventsourcing/esdbclient](https://pypi.org/project/esdbclient/)
- Node.js (JavaScript/TypeScript): [EventStore/EventStore-Client-NodeJS](https://github.com/EventStore/EventStore-Client-NodeJS)
- Java: [EventStore/TrogonDB-Client-Java](https://github.com/EventStore/TrogonDB-Client-Java)
- .NET: [EventStore/EventStore-Client-Dotnet](https://github.com/EventStore/EventStore-Client-Dotnet)
- Go: [EventStore/EventStore-Client-Go](https://github.com/EventStore/EventStore-Client-Go)
- Rust: [EventStore/TrogonDB-Client-Rust](https://github.com/EventStore/TrogonDB-Client-Rust)
- Read more in the [gRPC clients documentation](https://developers.trogondb.com/clients/grpc)

Community supported gRPC clients

- Elixir: [NFIBrokerage/spear](https://github.com/NFIBrokerage/spear)
- Ruby: [yousty/event_store_client](https://github.com/yousty/event_store_client)

## Community

- [Discord](https://discord.gg/aPXg6p7TH5)

## Building TrogonDB

TrogonDB is written in a mixture of C# and JavaScript. It can run on Windows,
Linux and macOS using Docker and the .NET runtime.

### Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)

Once you've installed the prerequisites for your system, you can launch a
`Release` build of TrogonDB as follows:

```bash
dotnet build -c Release src
```

The build scripts: `build.sh` and `build.ps1` are also available for Linux and
Windows respectively to simplify the build process.

To start a single node, you can then run:

```bash
node="./src/EventStore.ClusterNode/bin/Release/net10.0/EventStore.ClusterNode.dll"
dotnet "$node" --dev --db ./tmp/data --index ./tmp/index --log ./tmp/log
```

### Running the tests

You can launch the tests as follows:

```bash
dotnet test src/EventStore.sln
```

### Build TrogonDB Docker image

You can also build a Docker image by running the command:

```bash
docker build --tag myeventstore . \
--build-arg CONTAINER_RUNTIME={container-runtime} \
--build-arg RUNTIME={runtime}
```

For instance:

```bash
docker build --tag myeventstore . \
--build-arg CONTAINER_RUNTIME=noble \
--build-arg RUNTIME=linux-x64
```

**_Note:_** Because of the
[Docker issue](https://github.com/moby/buildkit/issues/1900), if you're building
a Docker image on Windows, you may need to set the `DOCKER_BUILDKIT=0`
environment variable. For instance, running in PowerShell:

```powershell
$env:DOCKER_BUILDKIT=0; docker build --tag myeventstore . `
--build-arg CONTAINER_RUNTIME=noble `
--build-arg RUNTIME=linux-x64
```

Currently, we support the following configurations:

1. Noble:

- `CONTAINER_RUNTIME=noble`
- `RUNTIME=linux-x64`

You can verify the built image by running:

```bash
docker run --rm myeventstore --insecure --what-if
```
