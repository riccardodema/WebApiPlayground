# ADR 0012 — Database as code (DACPAC) as the single source of truth

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Database as code](../../README.md#database-as-code) · lessons `[L27]` · `database/`, `.claude/context/database.md`

## Context

EF Core can create and migrate a schema, which is convenient — but it makes the *application* the owner
of the database, and `EnsureCreated` in tests produces a schema that **nobody deploys**. A DBA-reviewable,
diff-based deployment is the production-grade path, and the test schema must match the deployed one or the
suite stays green while production breaks.

## Decision

Version the schema as a **SQL Database Project** (`Microsoft.Build.Sql`) — declarative `CREATE` per
object — built into a **DACPAC** and deployed with **SqlPackage**, which computes the diff (no
hand-written `ALTER`s). The DACPAC is the **single source of truth**; the EF Core model is mapped **1:1**
to it; an idempotent post-deployment script seeds reference data. A dedicated **parity test suite**
deploys the *real* DACPAC (via DacFx) to a container database, runs the app against it, and compares the
schema **column-by-column** with the EF model — closing the gap `EnsureCreated` would hide.

## Consequences

- The same DACPAC publishes the schema in compose, in CI and in production — "DACPAC is the source of
  truth" is enforced, not aspirational.
- Drift between the EF model and the SQL project fails a test instead of a deployment (`[L27]`).
- Filtered indexes (e.g. the outbox `WHERE ProcessedAt IS NULL`) must be declared in **both** the DACPAC
  and the EF model so the two schemas stay identical (`[L22]`).
- Trade-off: two declarations of the schema (SQL project + EF mappings) kept in sync — which is exactly
  what the parity test guards.
