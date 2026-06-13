# Architecture Decision Records

An **ADR** captures *one* significant decision: the problem it solves, what was chosen, and what that
choice costs. Together these records are the "why" behind the code — the reasoning a diff can't show.

They're written for someone who wants to understand the engineering without reading every file. Each one
is short, self-contained, and links to the deep-dive section in the [README](../../README.md) and to the
code. Many were distilled from the pitfalls collected in
[`.claude/lessons.md`](../../.claude/lessons.md) (referenced as `[Lxx]`).

Format: lightweight [MADR](https://adr.github.io/madr/) — **Status · Context · Decision · Consequences**.
A decision stays in the log even if later superseded; that history is the point.

## Index

| # | Decision | Theme |
|---|---|---|
| [0001](0001-clean-architecture-enforced-by-tests.md) | Clean Architecture, layering **enforced by tests** | Structure |
| [0002](0002-openapi-with-scalar-not-swashbuckle.md) | OpenAPI via `Microsoft.AspNetCore.OpenApi` + Scalar (not Swashbuckle) | API surface |
| [0003](0003-uniform-error-contract-problemdetails.md) | One error contract: RFC 7807 ProblemDetails + correlation id | API surface |
| [0004](0004-config-gated-infrastructure.md) | Config-gated infrastructure (one artifact, dev → prod) | Cross-cutting |
| [0005](0005-caching-hybridcache-and-etag.md) | Caching: `HybridCache`/FusionCache split + HTTP ETag | Performance |
| [0006](0006-write-safety-idempotency-and-rate-limiting.md) | Write safety: idempotency keys + native rate limiting | Robustness |
| [0007](0007-api-versioning-by-url-segment.md) | API versioning by URL segment | Evolution |
| [0008](0008-optimistic-concurrency-rowversion-etag.md) | Optimistic concurrency: `rowversion` → ETag / `If-Match` | Correctness |
| [0009](0009-observability-opentelemetry.md) | Observability: OpenTelemetry, SDK only at the composition root | Operability |
| [0010](0010-resilience-polly-and-cache-as-resilience.md) | Resilience: explicit Polly pipeline + cache-as-resilience | Robustness |
| [0011](0011-reliable-messaging-outbox-service-bus.md) | Reliable async work: transactional outbox + Azure Service Bus | Distributed systems |
| [0012](0012-database-as-code-dacpac.md) | Database as code (DACPAC) as the single source of truth | Delivery |
| [0013](0013-secrets-via-key-vault.md) | Secrets via Azure Key Vault config provider, secretless-first | Security |
| [0014](0014-containerization-chiseled-and-compose.md) | Containerization: hardened chiseled image + compose stack | Delivery |
| [0015](0015-dual-cicd-azure-devops-and-github-actions.md) | CI/CD implemented twice (Azure DevOps + GitHub Actions) | Delivery |
| [0016](0016-proving-the-tests-work.md) | Proving the tests work: mutation testing + coverage ratchet | Quality |

## Recurring principles

A few ideas show up across many records, because they're the project's spine:

- **Graceful degradation by configuration** — Redis, OpenTelemetry, Service Bus and Key Vault are all
  *off until configured*. The same binary runs on a laptop with zero setup and in production with the lot
  (ADR&nbsp;[0004](0004-config-gated-infrastructure.md)).
- **Abstraction inward, implementation outward** — the Application layer depends only on plain
  interfaces; vendor SDKs (FusionCache, Polly, Service Bus, Key Vault) live in Infrastructure, and a test
  *fails the build* if one leaks upward (ADR&nbsp;[0001](0001-clean-architecture-enforced-by-tests.md)).
- **One correlation thread** — `CorrelationId` ↔ W3C `TraceId` ↔ the `traceId` in every error body, so a
  log, a trace and an HTTP response all point at the same request
  (ADR&nbsp;[0003](0003-uniform-error-contract-problemdetails.md), [0009](0009-observability-opentelemetry.md)).
- **Honest about status** — what is *implemented and tested* (against real dependencies or official
  emulators) is kept distinct from what is *authored but not yet deployed* (the live Azure rollout). No
  record overstates.
