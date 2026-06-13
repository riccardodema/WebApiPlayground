# ADR 0010 — Resilience for external calls: an explicit Polly pipeline, cache as resilience

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Resilience](../../README.md#resilience) · lessons `[L19]` `[L20]` · `Infrastructure/Popularity`

## Context

The moment the API calls **another service**, that service's latency and failures become *yours*: a slow
or flapping dependency can hang threads, exhaust the connection pool and cascade into an outage even
though your own code is fine. The endpoint `GET /api/v1/books/{id}/popularity` enriches a book from a
real external dependency (**Open Library**).

## Decision

Wrap the typed `HttpClient` in an **explicit Polly v8 pipeline**
(`Microsoft.Extensions.Http.Resilience`), composed by hand so every strategy is visible and tunable, in
the order that matters: **total timeout → retry (exponential backoff + jitter, transient only) →
circuit breaker → per-attempt timeout**. Exhaustion degrades to a domain
`ExternalServiceUnavailableException` → **503 ProblemDetails + `Retry-After`** (never a raw 500). The
response is **cached** with **degrade-to-stale**: when Open Library is down, fail-safe serves the last
good value instead of a 503. Only the abstraction `IBookPopularityClient` lives in Application; the
client and pipeline live in Infrastructure (NetArchTest fails the build if Polly leaks upward —
ADR&nbsp;[0001]).

## Consequences

- The dependency can be slow or down without taking the API with it; a burst collapses to one upstream
  call (stampede protection).
- `HttpClient.Timeout` must be `InfiniteTimeSpan` or it fights the pipeline; the cache's factory timeouts
  must be **infinite** too, or a 2s cache timeout silently pre-empts the resilience budget (`[L20]`).
- This is the cache exception noted in ADR&nbsp;[0005]: popularity uses `IFusionCache` in Infrastructure.
- Security-minded: the host is fixed config (no SSRF), the dependency is key-less, and upstream errors
  are never echoed to the client.

[0001]: 0001-clean-architecture-enforced-by-tests.md
[0005]: 0005-caching-hybridcache-and-etag.md
