# ADR 0005 — Caching: HybridCache/FusionCache split, plus HTTP ETag

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Caching](../../README.md#caching) · lessons `[L11]` `[L20]` · `.claude/context/caching.md`

## Context

Repeated `GET`s re-run a DB query, re-map, re-serialize and re-transfer identical bytes. Two different
costs are worth cutting: the server-side work, and the bytes on the wire. They call for two different
mechanisms, and the server-side cache must not drag a vendor SDK into the Application layer
(ADR&nbsp;[0001]).

## Decision

- **Server-side:** the Application caching decorator depends only on the standard **`HybridCache`**
  abstraction; **FusionCache** is the implementation, wired in the composition root. It brings
  **stampede protection** (one DB load per expired key, not a herd) and **fail-safe** (serve the last
  good value if the DB blips). Invalidation is **tag-based** — every write drops the `books` tag in one
  call. Setting a Redis connection string adds an **L2 + backplane** for multi-instance coherence
  (ADR&nbsp;[0004]).
- **HTTP-side:** each `GET` carries a strong **ETag**; `If-None-Match` yields **`304` with no body**.
- **Exception, made consciously:** the external-popularity cache (ADR&nbsp;[0010]) needs FusionCache-only
  options the `HybridCache` abstraction can't express (infinite factory timeouts, an extended fail-safe
  window), so *that one* cache uses `IFusionCache` directly — and therefore lives in Infrastructure.

## Consequences

- Hits served from L1 in microseconds; bandwidth saved on conditional requests.
- The clean asymmetry — `HybridCache` for the book cache, `IFusionCache` for popularity — is documented,
  not accidental (`[L20]`): the popularity options are *not* `HybridCache` options.
- Tests that seed the DB directly must also flush the cache, because the decorator only invalidates on
  writes that go *through* the service (`[L11]`).

[0001]: 0001-clean-architecture-enforced-by-tests.md
[0004]: 0004-config-gated-infrastructure.md
[0010]: 0010-resilience-polly-and-cache-as-resilience.md
