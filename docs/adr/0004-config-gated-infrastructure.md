# ADR 0004 — Config-gated infrastructure: one artifact, dev → prod

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** README (Caching, Observability, Outbox, Secrets) · lessons `[L11]` `[L18]` `[L24]` `[L25]`

## Context

The project leans on several heavyweight dependencies — Redis, an OpenTelemetry collector, Azure Service
Bus, Azure Key Vault. Requiring all of them to run anything would make the project unrunnable on a
laptop and would push environment differences into code (`#if`, separate builds), which is exactly where
production surprises come from.

## Decision

Each optional dependency is **gated by a single configuration value** and is **off when empty**:
`Redis` connection string, `OpenTelemetry:OtlpEndpoint`, `ServiceBus:FullyQualifiedNamespace`,
`KeyVault:Uri`. Empty ⇒ the feature degrades to a sensible in-process default (memory cache, no export,
in-process dispatch, config from appsettings). Set ⇒ the real implementation activates, **config-only,
no code change**. The reverse guard exists too: outside Development the app **fails fast** if a value
that production genuinely needs is missing, listing exactly which keys (and their env-var form).

## Consequences

- One image flows dev → CI → prod (12-factor); behaviour is chosen by configuration, not by build.
- `docker compose up` and the test suite run the *real* paths via official emulators, while a bare
  `dotnet run` still works with zero setup.
- A subtle trap: values read **during host build** (e.g. the Service Bus transport choice, `KeyVault:Uri`)
  must be injected via `UseSetting`, not `ConfigureAppConfiguration`, or a test silently exercises the
  fallback and passes for the wrong reason — so each gated branch has a structural probe asserting the
  real path is active (`[L25]`).
- Trade-off: more branches to test. Covered by the honesty tests in ADR&nbsp;[0016].

[0016]: 0016-proving-the-tests-work.md
