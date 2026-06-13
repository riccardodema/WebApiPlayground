# ADR 0003 — One error contract: RFC 7807 ProblemDetails with a correlation id

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Observability](../../README.md#observability) · lessons `[L08]` `[L10]` · `Api/ErrorHandling` · `.claude/context/error-handling.md`

## Context

An API that returns ad-hoc error shapes — a string here, a custom JSON object there, a bare 500
elsewhere — is hard to consume and hard to debug. Clients can't program against it, and an operator
staring at a 500 has no thread back to the log line that explains it.

## Decision

Every error is an **RFC 7807 `application/problem+json`** response, produced through
`AddProblemDetails()` + `IExceptionHandler`. Each body carries a **`correlationId`** (from the inbound
header or generated) and a W3C **`traceId`**, the same identifiers that appear in the structured logs.
Unhandled exceptions become a 500 ProblemDetails; validation failures a 400 with an `errors` map;
domain conditions map to the right status (412/428 concurrency, 503 dependency down, 429 throttling)
through dedicated handlers. The `Detail` field is populated **only in Development** to avoid leaking
internals in production.

## Consequences

- One shape to learn and to document — and the non-2xx statuses are declared in the OpenAPI contract,
  not discovered at runtime.
- The correlation id ties an HTTP error to its logs and its distributed trace (ADR&nbsp;[0009]).
- Two implementation traps shaped the design: a `ValidationProblemDetails` serialised on the static
  `ProblemDetails` type silently drops its `errors` map, so validation writes the concrete type directly
  (`[L10]`); and the exception handler runs outside the Serilog `LogContext`, so the correlation id is
  stashed in `HttpContext.Items` to survive into the error body (`[L08]`).

[0009]: 0009-observability-opentelemetry.md
