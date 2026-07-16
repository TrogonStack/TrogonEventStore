# Usage Telemetry

TrogonEventStore has two different observability concepts:

- OpenTelemetry and metrics, which operators explicitly configure for their own
  monitoring systems.
- Legacy usage telemetry, which is an inherited service and should not be
  treated as the primary TrogonEventStore observability path.

Use [OpenTelemetry integration](diagnostics/integrations.md) and
[Metrics](diagnostics/metrics.md) for production instrumentation.

## Operator guidance

If your environment should not send usage telemetry, set:

```bash:no-line-numbers
EVENTSTORE_TELEMETRY_OPTOUT=true
```

The option name currently keeps the inherited `EVENTSTORE_` prefix for runtime
compatibility.

## What to use instead

For normal operations:

- Scrape Prometheus metrics from `/-/metrics`.
- Export OTLP logs, metrics, and traces to your collector.
- Use server logs for startup, shutdown, certificate, and cluster diagnostics.
- Use the Admin UI for operator workflows.

## Future policy

Usage telemetry should be revisited as a Trogon-owned product policy before it
is documented as a recommended production feature. Until then, OTLP and metrics
are the preferred observability surfaces.
