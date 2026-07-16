---
title: "How-to"
---

## Configuration options

TrogonEventStore has a number of configuration options that can be changed. You can find all the options described
in details in this section.

When you don't change the configuration, TrogonEventStore will use sensible defaults, but they might not suit your
needs. You can always instruct TrogonEventStore to use a different set of options. There are multiple ways to
configure TrogonEventStore server, described below.

### Version and help

You can check what version of TrogonEventStore you have installed by using the `--version` parameter in the
command line. For example:

:::: code-group
::: code Linux
```bash:no-line-numbers
$ eventstored --version
TrogonEventStore version <version>
```
:::
::: code Windows
```powershell:no-line-numbers
> EventStore.ClusterNode.exe --version
TrogonEventStore version <version>
```
:::
::::

The full list of available options is available from the currently installed server by using the `--help`
option in the command line.

### Configuration file

You would use the configuration file when you want the server to run with the same set of options every time.
YAML files are better for large installations as you can centrally distribute and manage them, or generate
them from a configuration management system.

The default configuration file name is `eventstore.conf` and it's located in
- **Linux:** `/etc/eventstore/`
- **Windows:** TrogonEventStore installation directory

The configuration file has YAML-compatible format. The basic format of the YAML configuration file is as
follows:

```yaml:no-line-numbers
---
Db: "/volumes/data"
Log: "/esdb/logs"
```

::: tip 
You need to use the three dashes and spacing in your YAML file.
:::

The default configuration file name is `eventstore.conf`. It is located in `/etc/eventstore/` on Linux and the
server installation directory on Windows. You can either change this file or create another file and instruct
TrogonEventStore to use it.

To tell the TrogonEventStore server to use a different configuration file, you pass the file path on the command
line with `--config=filename`, or use the `EVENTSTORE_CONFIG`
environment variable.

### Environment variables

You can also set all arguments with environment variables. All variables are prefixed with `EVENTSTORE_` and
normally follow the pattern `EVENTSTORE_{option}`. For example, setting the `EVENTSTORE_LOG`
variable would instruct the server to use a custom location for log files.

Environment variables override all the options specified in configuration files.

### Command line

You can also override options from both configuration files and environment variables using the command line.

For example, starting TrogonEventStore with the `--log` option will override the default log files location:

:::: code-group
::: code-group-item Linux
```bash:no-line-numbers
eventstored --log /tmp/eventstore/logs
```
:::
::: code-group-item Windows
```bash:no-line-numbers
EventStore.ClusterNode.exe --log C:\Temp\EventStore\Logs
```
:::
::::

### Testing the configuration

If more than one method is used to configure the server, it might be hard to find out what the effective
configuration will be when the server starts. To help to find out just that, you can use the `--what-if`
option.

When you run TrogonEventStore with this option, it will print out the effective configuration applied from all
available sources (default and custom configuration file, environment variables and command line parameters)
and print it out to the console.

The exact output is generated from the current option model and lists each effective value and its
configuration source.

::: note 
Version 21.6 introduced a stricter configuration check: the server will _not start_ when an unknown
configuration options is passed in either the configuration file, environment variable or command line.

E.g: the following will prevent the server from starting:

* `--UnknownConfig` on the command line
* `EVENTSTORE_UnknownConfig` through environment variable
* `UnknownConfig: value` in the config file

And will output on `stdout`
only: _Error while parsing options: The option UnknownConfig is not a known option. (Parameter 'UnknownConfig')_.
:::

## Autoconfigured options

Some options are configured at startup to make better use of the available resources on larger instances or
machines.

These options are `StreamInfoCacheCapacity`, `ReadConcurrencyLimit`, and `WorkerThreads`.

When these options remain `0`, non-container deployments derive defaults from available resources. Containers
use fixed defaults of 100,000 stream-info entries, 4 reader threads, and 5 worker threads. Explicit positive
values take precedence.

### StreamInfoCacheCapacity

This option sets the maximum number of entries to keep in the stream info cache. This is the lookup that
contains the information of any stream that has recently been read or written to. Having entries in this cache
significantly improves write and read performance to cached streams on larger databases.

By default, the cache dynamically resizes according to the amount of free memory. The minimum that it can be set to is 100,000 entries.

| Format               | Syntax                                  |
|:---------------------|:----------------------------------------|
| Command line         | `--stream-info-cache-capacity`          |
| YAML                 | `StreamInfoCacheCapacity`               |
| Environment variable | `EVENTSTORE_STREAM_INFO_CACHE_CAPACITY` |

The option is set to 0 by default, which enables dynamic resizing. The default on previous versions of
TrogonEventStore was 100,000 entries.

::: note
The default value of 0 for `StreamInfoCacheCapacity` might not always be the best value for optimal performance. Ideally, it should be set to double the number of streams in the anticipated working set.

The total number of streams can be obtained by checking the event count in the `$streams` system stream. This stream is created by the [$streams system projection](projections.md#streams-projection).

It should be noted that the total number of streams does not necessarily give you the anticipated working set. The working set of streams is the set of streams that you intend on actively reading, writing, and/or subscribing to. This can be much lower than the total number of streams in certain cases, especially in systems that have many short-lived streams.
:::

### ReadConcurrencyLimit

This option configures the number of read requests TrogonEventStore can process concurrently. Having more reader threads
allows more concurrent reads to be processed.

The reader threads count will be set at startup to twice the number of available processors, with a minimum of
4 and a maximum of 16 threads.

| Format               | Syntax                            |
|:---------------------|:----------------------------------|
| Command line         | `--read-concurrency-limit`        |
| YAML                 | `ReadConcurrencyLimit`            |
| Environment variable | `EVENTSTORE_READ_CONCURRENCY_LIMIT` |

The option is set to 0 by default, which enables autoconfiguration. The default on previous versions of
TrogonEventStore was 4 threads.

::: warning 
Increasing the reader threads count too high can cause read timeouts if your disk cannot handle the
increased load.
:::

### WorkerThreads

The `WorkerThreads` option configures the number of threads available to the pool of worker services.

At startup the number of worker threads will be set to 10 if there are more than 4 reader threads. Otherwise,
it will be set to have 5 threads available.

| Format               | Syntax                      |
|:---------------------|:----------------------------|
| Command line         | `--worker-threads`          |
| YAML                 | `WorkerThreads`             |
| Environment variable | `EVENTSTORE_WORKER_THREADS` |

The option is set to 0 by default, which enables autoconfiguration. The default on previous versions of
TrogonEventStore was 5 threads.

## Component configuration

The supported server configuration surface is documented in this guide and in
the feature-specific pages linked from it.

Some runtime configuration keys retain the inherited `EventStore` prefix for
compatibility. Do not treat undocumented plugin keys as supported product
features.

Environment variables can configure nested sections by using `__` as the level
delimiter.
