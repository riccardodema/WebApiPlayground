# ADR 0009 — Observability: OpenTelemetry, SDK only at the composition root

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Observability](../../README.md#observability) · lessons `[L18]` · `Api/Extensions/OpenTelemetryExtensions.cs`, `Application/Diagnostics`

## Context

Structured logs and a correlation id existed, but there were no **distributed traces**, no **metrics**,
and no **vendor-neutral** channel to a backend. The instrumentation also must not drag an SDK into the
inner layers and break the layering rules (ADR&nbsp;[0001]).

## Decision

Adopt **OpenTelemetry** (the CNCF standard): traces + metrics + logs through one API, exported over
**OTLP** to any backend (Jaeger, Tempo, Prometheus, Aspire Dashboard, App Insights…). The key boundary:
**business code is instrumented with BCL primitives** (`System.Diagnostics.ActivitySource` / `Meter`) in
the Application layer; the **OTel SDK and exporters live only in the Api**. A custom `Books.Create` span
and a `books.created` metric sit alongside the free framework auto-instrumentation. Logs flow through a
**Serilog → OTLP bridge** that re-attaches `TraceId`/`SpanId` and carries the `CorrelationId`. Export is
**config-gated** (ADR&nbsp;[0004]): empty `OtlpEndpoint` ⇒ collected but not exported.

## Consequences

- The correlation loop closes: `CorrelationId` ↔ W3C `TraceId` ↔ the `traceId` in every ProblemDetails
  (ADR&nbsp;[0003]) — jump from an error, to a log, to the full trace.
- Free framework metrics include the **rate limiter** meter (leases, queues, rejections).
- The EF Core instrumentation is `-beta` (unstable span names) — included consciously, asserted *softly*
  in tests. `ActivityListener`/metric counters are process-global, so tests filter by a unique tag and
  assert the deterministic minimum, not exact totals (`[L18]`).

[0001]: 0001-clean-architecture-enforced-by-tests.md
[0003]: 0003-uniform-error-contract-problemdetails.md
[0004]: 0004-config-gated-infrastructure.md
