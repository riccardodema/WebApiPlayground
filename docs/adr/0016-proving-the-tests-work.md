# ADR 0016 — Proving the tests work: mutation testing + coverage ratchet + honesty tests

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Test quality](../../README.md#test-quality--proving-the-tests-themselves-work) · lessons `[L27]` `[L28]` `[L29]` `[L30]` · `.claude/context/testing.md`

## Context

Green tests aren't evidence by themselves. This repo once had an end-to-end test passing on the **wrong
transport** (in-process instead of Service Bus) and still green, because the downstream handler was the
same (`[L25]`). A test suite needs mechanisms that prove *the tests would actually catch a regression*.

## Decision

Three mechanisms keep the suite honest:

- **Mutation testing (Stryker.NET):** mutate the production code and require the tests to *fail*.
  Incremental on every PR (`--since`), full run **on demand** via a dedicated workflow (no schedules) —
  HTML report + a self-hosted `mutation score` badge. The score was driven from ~25% to **~82%** by
  reading the survived-mutant report and testing **behaviour, not implementation**.
- **Coverage ratchet:** line + branch coverage (unit + integration, merged) is gated against a committed
  thresholds file that can only go **up**; badges are self-hosted (no third-party service).
- **Structural honesty tests:** a **DACPAC↔EF parity** suite (ADR&nbsp;[0012]) and a **real-JWT** suite
  that runs the production `JwtBearer`/Entra pipeline against an in-process fake OIDC authority — proving
  things the test auth handler never could.

## Consequences

- A weak assertion shows up as a surviving mutant; lost coverage fails the gate; a config-gated branch
  has a probe that the real path is active (ADR&nbsp;[0004]).
- Mutation runs are **per-layer** (solution mode enlists the whole suite, including slow integration
  tests) and exclude the OpenAPI source-generator interceptor file, which can't be mutated without
  breaking compilation (`[L29]`).
- Some mutants are honestly **not killable** without testing implementation details (internal ref-counts,
  assembly version, emulator TLS branches) — left and documented; the point of mutation testing is the
  signal, not a vanity 100% (`[L30]`).

[0004]: 0004-config-gated-infrastructure.md
[0012]: 0012-database-as-code-dacpac.md
