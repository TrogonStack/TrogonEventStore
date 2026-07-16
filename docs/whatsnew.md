# What's New

This page summarizes the current TrogonEventStore documentation baseline. It is
not a release-note mirror for another distribution.

## Current baseline

- TrogonEventStore is documented as a FOSS-only server distribution.
- Application event access is documented as gRPC-first.
- HTTP documentation is limited to browser UI, health probes, metrics, and
  infrastructure concerns.
- Health probes are exposed on `/-/liveness` and `/-/readiness`.
- Prometheus scraping is exposed on `/-/metrics`.
- OpenTelemetry documentation covers explicit OTLP export for logs, metrics,
  and traces.
- The Admin UI is the embedded Blazor UI served by the node.
- User-defined projection execution, connector runtimes, SQL-like query
  surfaces, and rich read models are documented as external component work by
  default.

## Recent documentation cleanup

The documentation has been consolidated around the current local product shape:

- Installation now focuses on source builds, local Docker images, and explicit
  production configuration.
- Upgrade guidance now focuses on safe local rollout practices instead of stale
  historical release notes.
- Security documentation now focuses on supported authentication methods and
  avoids unsupported plugin surfaces.
- Diagnostics documentation points operators to logs, metrics, and
  OpenTelemetry rather than legacy HTTP management endpoints.

## Compatibility note

The server still uses some `EventStore` namespaces, configuration prefixes, and
wire-compatible concepts internally. Those names are part of the inherited
runtime and client compatibility surface. User-facing documentation should still
describe the distribution as TrogonEventStore or TrogonDB.
