# TrogonEventStore Semantic Conventions

This package gives TrogonEventStore components one generated source for
OpenTelemetry attributes and built-in metric definitions. Each
`MetricDefinition` carries the instrument name, UCUM unit, description, and
instrument kind from the Weaver registry. `MetricDefinitions.All` provides
every built-in definition exactly once in deterministic name order.

Metric units use UCUM notation. `s` means seconds, `By` means bytes, and `1`
marks a dimensionless value such as a ratio or logical position. Annotated
units such as `{event}` identify counts without embedding the unit in the
instrument name.

`OpenTelemetryMetricDefinitions` provides the selected process and filesystem
definitions from the pinned official OpenTelemetry registry. Its `All`
collection is generated in deterministic name order alongside the custom
catalog.

`AttributeNames` contains the selected official resource attributes.
`TrogonAttributeNames` contains every custom `trogon.eventstore.*` metric
attribute, with deterministic `All` ordering.

Instrumentation consumes `MetricDefinitions` so metric metadata cannot drift
independently. The package has no runtime dependencies.

The pinned OpenTelemetry registry version and C# templates under `otel/semconv`
are the source of truth. Regenerate the committed constants with
`mise run semconv:generate` and verify them with `mise run semconv:check`.
