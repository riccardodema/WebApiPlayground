# ADR 0006 — Write safety: idempotency keys and native rate limiting

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Idempotency](../../README.md#idempotency), [→ Rate limiting](../../README.md#rate-limiting) · lessons `[L14]` `[L15]`

## Context

A public write endpoint faces two everyday hazards. First, **retries**: `POST` isn't idempotent, so a
lost response (timeout, dropped connection, double-click, an auto-retrying client) creates a **duplicate**
resource. Second, **abuse**: one client hammering the API (a retry storm, a scraper, a bug) can starve
everyone else and saturate the database.

## Decision

- **Idempotency:** a client supplies an **`Idempotency-Key`** header; the first request runs and its
  response is stored, and any retry with the same key **replays that stored response verbatim** (status,
  `Location`, body) marked `Idempotency-Replayed: true` — an exactly-once effect. The same key with a
  *different* payload returns **422**; 5xx is never stored, so transient failures stay retriable.
- **Rate limiting:** the **native .NET rate limiter** (no extra package) with **sliding-window** policies
  — reads `100/60s`, writes `20/60s`, independent buckets — **partitioned per client** (the authenticated
  claim, IP for anonymous). Over the limit ⇒ **429 ProblemDetails** with `Retry-After`.

Both are wired as middleware/filters and partitioned by the same client identity, so they compose: a
same-key retry storm is replayed, a different-payload storm is throttled.

## Consequences

- No duplicates on retry; no single client can eat everyone's quota.
- Replay must be **verbatim**, which forced a *middleware* that buffers the real response stream — a
  filter can't see the generated `Location`/body yet (`[L14]`).
- Options are read **lazily at request time** (`IOptions`), not at registration, or test overrides are
  ignored and the limiter sees only `appsettings.json` (`[L15]`).
- Both stores are `IDistributedCache`/in-memory today, **Redis-ready** by config (ADR&nbsp;[0004]).

[0004]: 0004-config-gated-infrastructure.md
