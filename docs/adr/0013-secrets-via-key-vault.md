# ADR 0013 — Secrets via Azure Key Vault config provider, secretless-first

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Secrets](../../README.md#secrets--azure-key-vault-config-provider) · [docs/keyvault.md](../keyvault.md) · lessons `[L25]` `[L26]`

## Context

Production secrets — chiefly the database connection string — must not live in the repo, in an image, or
in a deployment template. They should be fetched at runtime from a managed store, while the local and
test experience stays zero-friction (no real Azure account required).

## Decision

Load secrets from **Azure Key Vault** at startup via the official **configuration provider**, gated on
`KeyVault:Uri` (ADR&nbsp;[0004]). The provider is added **last** (vault values win over
appsettings/env) and **before** the startup fail-fast (so vault secrets satisfy the validator).
**Secretless-first:** only *real* secrets go in the vault (`ConnectionStrings--Default`) — Service Bus
stays secretless via managed identity, and `AzureAd` values are identifiers, not secrets. Credentials are
**explicit per environment** (`ManagedIdentity` / `AzureCli` / `Emulator`), never a
`DefaultAzureCredential` chain. Locally, `docker compose` and the tests run a **community Key Vault
emulator** (pinned image, self-made Testcontainers wrapper) so the real code path is exercised.

## Consequences

- Switching from emulator to the real vault changes **configuration, not code**.
- An unreachable/forbidden vault makes the app **refuse to start** with a *talking* fatal error — where
  it pointed, which credential, the probable causes, the remedy — and a **non-zero exit code** (a
  previous bootstrap `catch` exited 0, hiding the refusal from orchestrators) (`[L26]`).
- The community NuGet emulator package installs a CA into the host trust store, so it's deliberately
  **avoided** in favour of a per-run cert scoped to the dev client only (`[L26]`).
- `KeyVault:Uri` is read during host build, so tests inject it via `UseSetting` (ADR&nbsp;[0004], `[L25]`).

[0004]: 0004-config-gated-infrastructure.md
