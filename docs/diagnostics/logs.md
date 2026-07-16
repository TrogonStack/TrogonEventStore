---
title: "Logs"
---

# Database logs

TrogonEventStore logs its internal operations to the console (stdout) and to log files. The default location of
the log files and the way to change it is described [below](#logs-location).

There are a few options to change the way how TrogonEventStore produces logs and how detailed the logs should be.

::: warning
The TrogonEventStore logs may contain sensitive information such as stream names, usernames, and projection definitions.
:::

## Log format

TrogonEventStore uses the structured logging in JSON format that is more machine-friendly and can be ingested by
vendor-specific tools like Logstash or Datadog agent.

Here is how the structured log looks like:

```json
{
  "PID": "6940",
  "ThreadID": "23",
  "Date": "2020-06-16T16:14:02.052976Z",
  "Level": "Debug",
  "Logger": "ProjectionManager",
  "Message": "PROJECTIONS: Starting Projections Manager. (Node State : {state})",
  "EventProperties": {
    "state": "Master"
  }
}
{
  "PID": "6940",
  "ThreadID": "15",
  "Date": "2020-06-16T16:14:02.052976Z",
  "Level": "Info",
  "Logger": "ClusterVNodeController",
  "Message": "========== [{internalHttp}] Sub System '{subSystemName}' initialized.",
  "EventProperties": {
    "internalHttp": "127.0.0.1:2112",
    "subSystemName": "Projections"
  }
}
{
  "PID": "6940",
  "ThreadID": "23",
  "Date": "2020-06-16T16:14:02.052976Z",
  "Level": "Debug",
  "Logger": "MultiStreamMessageWriter",
  "Message": "PROJECTIONS: Resetting Worker Writer",
  "EventProperties": {}
}
{
  "PID": "6940",
  "ThreadID": "23",
  "Date": "2020-06-16T16:14:02.055000Z",
  "Level": "Debug",
  "Logger": "ProjectionCoreCoordinator",
  "Message": "PROJECTIONS: SubComponent Started: {subComponent}",
  "EventProperties": {
    "subComponent": "EventReaderCoreService"
  }
}
```

This format is aligned with [Serilog Compact JSON format](https://github.com/serilog/serilog-formatting-compact).

## Logs location

Log files are located in `/var/log/eventstore` for Linux and macOS, and in the `logs` subdirectory of the
TrogonEventStore installation directory on Windows. You can change the log files location using the `Log`
configuration option.

::: tip
Moving logs to a separate storage might improve the database performance if you keep the default
verbose log level.
:::

| Format               | Syntax           |
|:---------------------|:-----------------|
| Command line         | `--log`          |
| YAML                 | `Log`            |
| Environment variable | `EVENTSTORE_LOG` |

For example, adding this line to the `eventstore.conf` file will force writing logs to
the `/tmp/eventstore/logs` directory:

```text:no-line-numbers
Log: /tmp/eventstore/logs
```

## Log level

You can change the level using the `LogLevel` setting:

| Format               | Syntax                 |
|:---------------------|:-----------------------|
| Command line         | `--log-level`          |
| YAML                 | `LogLevel`             |
| Environment variable | `EVENTSTORE_LOG_LEVEL` |

Acceptable values are: `Default`, `Verbose`, `Debug`, `Information`, `Warning`, `Error`, and `Fatal`.

## Logging options

You can tune the TrogonEventStore logging further by using the logging options described below.

### Log configuration file

Specifies the location of the file which configures the logging levels of various components.

| Format               | Syntax                  |
|:---------------------|:------------------------|
| Command line         | `--log-config`          |
| YAML                 | `LogConfig`             |
| Environment variable | `EVENTSTORE_LOG_CONFIG` |

By default, the application directory (and `/etc/eventstore` on Linux and Mac) are checked. You may specify a
full path.

### HTTP requests logging

TrogonEventStore can also log all the incoming HTTP requests, like many HTTP servers do. Requests are logged
before being processed, so unsuccessful requests are logged too.

Use one of the following ways to enable the HTTP requests logging:

| Format               | Syntax                         |
|:---------------------|:-------------------------------|
| Command line         | `--log-http-requests`          |
| YAML                 | `LogHttpRequests`              |
| Environment variable | `EVENTSTORE_LOG_HTTP_REQUESTS` |

**Default**: `false`, logging HTTP requests is disabled by default.

### Log failed authentication

For security monitoring, you can enable logging failed authentication attempts by
setting `LogFailedAuthenticationAttempts` setting to true.

| Format               | Syntax                                          |
|:---------------------|:------------------------------------------------|
| Command line         | `--log-failed-authentication-attempts`          |
| YAML                 | `LogFailedAuthenticationAttempts`               |
| Environment variable | `EVENTSTORE_LOG_FAILED_AUTHENTICATION_ATTEMPTS` |

**Default**: `false`

### Log console format

The format of the console logger. Use `Json` for structured log output.

| Format               | Syntax                          |
|:---------------------|:--------------------------------|
| Command line         | `--log-console-format`          |
| YAML                 | `LogConsoleFormat`              |
| Environment variable | `EVENTSTORE_LOG_CONSOLE_FORMAT` |

Acceptable values are: `Plain`, `Json`

**Default**: `Plain`

### Log file size

The maximum size of each log file, in bytes.

| Format               | Syntax                     |
|:---------------------|:---------------------------|
| Command line         | `--log-file-size`          |
| YAML                 | `LogFileSize`              |
| Environment variable | `EVENTSTORE_LOG_FILE_SIZE` |

**Default**: `1GB`

### Log file interval

How often to rotate logs.

| Format               | Syntax                         |
|:---------------------|:-------------------------------|
| Command line         | `--log-file-interval`          |
| YAML                 | `LogFileInterval`              |
| Environment variable | `EVENTSTORE_LOG_FILE_INTERVAL` |

Acceptable values are: `Minute`, `Hour`, `Day`, `Week`, `Month`, `Year`

**Default**: `Day`

### Log file retention count

Defines how many log files need to be kept on disk. By default, logs for the last month are available. Tune this setting if you need to have more history in the logs, or you need to save disk space. 

| Format               | Syntax                           |
|:---------------------|:---------------------------------|
| Command line         | `--log-file-retention-count`     |
| YAML                 | `LogFileRetentionCount`          |
| Environment variable | `EVENTSTORE_LOG_RETENTION_COUNT` |

**Default**: `31`

#### Disable log file

You can completely disable logging to a file by changing the `DisableLogFile` option.

| Format               | Syntax                        |
|:---------------------|:------------------------------|
| Command line         | `--disable-log-file`          |
| YAML                 | `DisableLogFile`              |
| Environment variable | `EVENTSTORE_DISABLE_LOG_FILE` |

**Default**: `false`

## Collecting logs

TrogonEventStore does not document a remote log-download HTTP endpoint. Collect
logs from the configured log directory, the container runtime, or the platform
logging system.

For Kubernetes, prefer writing logs to standard output and collecting them with
the cluster logging pipeline. For virtual machines or bare metal, ship the log
directory with the host log agent.
