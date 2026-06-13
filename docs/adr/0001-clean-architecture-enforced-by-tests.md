# ADR 0001 — Clean Architecture, layering enforced by tests

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Architecture](../../README.md#architecture) · `tests/WebApiPlayground.ArchitectureTests` · `.claude/context/architecture.md`

## Context

A layered architecture only delivers its benefits — testability, swappable infrastructure, a domain that
doesn't rot — if the dependency rules actually hold. Written down in a document, they erode on the first
deadline: someone references `DbContext` from a service, or pulls ASP.NET into the domain, and nobody
notices until the design is gone.

## Decision

Four projects — **Domain → Application → Infrastructure / Api** — with dependencies pointing **inward**:
Domain has zero dependencies; Application defines interfaces (`IBookRepository`, `IBooksService`);
Infrastructure implements them; the Api is the composition root that wires everything via per-layer
`DependencyInjection.cs`. The rules are **executed as NetArchTest assertions** in a dedicated test
project, run in CI alongside the unit tests: Domain and Application may not reference EF Core or ASP.NET,
lower layers may not reference the Api, and vendor SDKs may not leak into Application.

## Consequences

- The architecture is **self-validating**: a forbidden reference fails the build, not a code review.
- Every later feature inherits a clear home for its abstraction (Application) vs its implementation
  (Infrastructure) — a pattern repeated for caching, resilience, messaging and secrets.
- Cost: some indirection (an interface + a registration) even for small features. Accepted — it's what
  keeps the seams testable.
- The same rule set is the reason cross-cutting SDKs (FusionCache, Polly, `Azure.Messaging`) each needed
  a deliberate placement decision rather than being dropped wherever convenient.
