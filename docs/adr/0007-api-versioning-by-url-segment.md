# ADR 0007 — API versioning by URL segment

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → API versioning](../../README.md#api-versioning) · lessons `[L16]` · `Api/Versioning`

## Context

An API with real clients can't change its contract freely: renaming a field, nesting an object or
dropping a property **breaks** existing callers. Versions must be able to **coexist** so old clients stay
put while new ones adopt the evolved shape. Several schemes exist (URL segment, header, query string,
media-type).

## Decision

Use **Asp.Versioning** (the maintained successor of `Mvc.Versioning`) with the **URL-segment** scheme:
`/api/v1/books`, `/api/v2/books`. It's the most visible and the most discoverable — a reader sees it in
Scalar and can try it from a browser — and it yields **one OpenAPI document per version**. A worked
**v2** demonstrates a real breaking change: the read representation moves the author from a flat string
to a **nested object**, while the **write contract is shared** across versions (one controller for
writes, a v2 controller for the evolved reads). `ReportApiVersions` advertises `api-supported-versions`
on every response.

## Consequences

- Clients discover available versions from any call; deprecation + RFC 8594 `Sunset` are the documented
  retirement path (not triggered, since nothing is retired).
- Every versioned operation still composes with auth, rate limiting, idempotency, ETag caching and
  ProblemDetails; the OpenAPI transformers are **shared** across documents (ADR&nbsp;[0002]).
- `Asp.Versioning.OpenApi` isn't published on NuGet, so per-version documents are built from the native
  `AddOpenApi("v1"/"v2")` keyed to the ApiExplorer group; non-versioned test endpoints need
  `[ApiVersionNeutral]`, and an unknown URL version is a **404** by design, not a 400 (`[L16]`).

[0002]: 0002-openapi-with-scalar-not-swashbuckle.md
