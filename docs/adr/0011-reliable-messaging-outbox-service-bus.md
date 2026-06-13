# ADR 0011 — Reliable async work: transactional outbox + Azure Service Bus

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Transactional outbox + Azure Service Bus](../../README.md#transactional-outbox--azure-service-bus) · lessons `[L21]` `[L22]` `[L24]` · `.claude/context/outbox.md`

## Context

Computing a book's popularity calls Open Library — slow and third-party — so it must happen **off the
request thread**. A first cut used an in-process `BackgroundService` over a bounded `Channel`: simple,
but the enqueue isn't transactional with the DB write, so items are lost on a crash and dropped on a full
queue (**at-most-once**). Acceptable for a best-effort snapshot, but not the reliable design.

## Decision

Adopt the canonical **transactional outbox** with a **broker (Azure Service Bus)**. `POST/PUT /books`
writes the book **and** an `OutboxMessages` row in the **same EF transaction** — crash before commit
rolls back both, so no event exists for a write that didn't happen, and vice versa (**at-least-once,
durable**). A polling `OutboxDispatcher` reads unprocessed rows and **publishes** them through
`IIntegrationEventPublisher`; a **decoupled consumer** receives and enriches. `ProcessedAt` marks the
durable **hand-off to the broker**. Settlement is manual (`Complete` / `Abandon` → redelivery →
dead-letter); the consumer is **idempotent** (snapshot upsert is 1:1 with the book), so redelivery is
safe. The transport is one seam, config-gated (ADR&nbsp;[0004]): Service Bus is the **real** path
(compose emulator + Production managed identity, **no SAS**), with an in-process fallback only for a bare
offline `dotnet run`.

## Consequences

- The write and its event are atomic; delivery is durable and idempotent end-to-end.
- The work-unit (`OutboxProcessor`) is split from the hosting loop (`OutboxDispatcher`) so tests can drive
  processing deterministically — a continuously-polling hosted service over a shared DB is otherwise
  deeply flaky (`[L22]`).
- Verified end-to-end with the **official Service Bus emulator** (Testcontainers + compose), no Azure
  account. The Bicep module is authored and validated but **not yet deployed** — stated as such.
- The consumer is in-process today, pre-wired to split into a dedicated Worker later.

[0004]: 0004-config-gated-infrastructure.md
