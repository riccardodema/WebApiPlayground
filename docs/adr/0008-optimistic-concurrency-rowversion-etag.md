# ADR 0008 — Optimistic concurrency: rowversion exposed as ETag / If-Match

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Optimistic concurrency](../../README.md#optimistic-concurrency) · lessons `[L17]` · `.claude/context/optimistic-concurrency.md`

## Context

Two clients read the same book and `PUT`/`DELETE` it almost simultaneously: without protection the
second write silently overwrites the first — a **lost update**. Even on a single instance, concurrent
users make this real. Pessimistic locks are a poor fit for stateless HTTP.

## Decision

Each book carries a **version token** — a SQL Server `rowversion` column, auto-bumped on every UPDATE,
mapped by EF Core with `.IsRowVersion()` as a **concurrency token** (the `UPDATE`/`DELETE` becomes
conditional; 0 rows ⇒ `DbUpdateConcurrencyException`). That token is **exposed as the book's ETag** and
validated with **`If-Match`**, *reusing the existing ETag infrastructure* — one header serves both
conditional caching (`304`) and concurrency. Writes **require** `If-Match`: missing ⇒ **428 Precondition
Required**, stale ⇒ **412 Precondition Failed**; a successful write returns the new ETag so a client can
chain updates without re-`GET`ting.

## Consequences

- No lost updates, no locks; applies to `PUT` and `DELETE`, across v1 and v2 (writes are shared).
- The naive "find-then-update" never detects a conflict (it compares against the row it just read), so
  the expected version is forced onto the token's `OriginalValue` from `If-Match` (`[L17]`).
- A conscious caveat: the token is `[JsonIgnore]` (header-only). With the **L2 Redis** cache active
  (ADR&nbsp;[0004]) the cached DTO is serialized and the token is stripped, so the ETag would fall back
  to a representation hash — a documented limitation until L2 is enabled, with a known fix.

[0004]: 0004-config-gated-infrastructure.md
