# ADR 0014 — Containerization: a hardened chiseled image + compose as the full local stack

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Containerization](../../README.md#containerization) · lessons `[L23]` · `Dockerfile`, `docker-compose*.yml`

## Context

Until this point Docker served only the tests (Testcontainers spins up SQL *for the test process*; the
app runs in-process). Two pieces were missing: the app as a **portable, reproducible artifact**, and the
**whole runtime stack in one command** for onboarding and for exercising the real app against real
dependencies.

## Decision

A **multi-stage Dockerfile**: build on the .NET 10 SDK image (csproj copied first so the `restore` layer
caches), run on **`aspnet:10.0-noble-chiseled-extra`** — a distroless-style image with **no shell or
package manager**, running as the **non-root `app`** user on port **8080**. **`docker compose up`** brings
up the full stack: API + SQL Server + schema **published from the DACPAC** (a one-shot `db-migrations`
service reusing `deploy.sh`) + the **official Service Bus emulator** + a **Key Vault emulator**, wired
with health checks and ordered startup. Redis and the Aspire Dashboard are opt-in override files. Outside
Development the app **fails fast** on missing config, listing exactly what's absent.

## Consequences

- One command, no local .NET or SQL Server, to run the whole thing — and the **real** outbox and secrets
  paths run locally over the emulators (ADR&nbsp;[0011], [0013]).
- The `-extra` variant is mandatory: plain chiseled has no ICU, and `Microsoft.Data.SqlClient` doesn't
  support Globalization Invariant Mode — the app would start but every DB query would fail (`[L23]`).
- No `HEALTHCHECK` in the image (chiseled has no shell); liveness is an **external** HTTP probe.
- On Apple Silicon the SQL and Service Bus emulator images run under amd64 emulation.
- Validated by **contract tests** (non-root, no plaintext secrets, port 8080, migrations-before-api) plus
  a **live smoke test** that builds the image and hits `/health/live` (ADR&nbsp;[0016]).

[0011]: 0011-reliable-messaging-outbox-service-bus.md
[0013]: 0013-secrets-via-key-vault.md
[0016]: 0016-proving-the-tests-work.md
