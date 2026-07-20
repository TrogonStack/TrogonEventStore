# TrogonEventStore Semantic Conventions

This package gives TrogonEventStore components one generated source for OpenTelemetry attribute names. It has no runtime dependencies.

The pinned OpenTelemetry registry version and C# templates under `otel/semconv` are the source of truth. Regenerate the committed constants with `mise run semconv:generate` and verify them with `mise run semconv:check`.
