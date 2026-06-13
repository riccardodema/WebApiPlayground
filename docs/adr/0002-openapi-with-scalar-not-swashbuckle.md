# ADR 0002 — OpenAPI via Microsoft.AspNetCore.OpenApi + Scalar, not Swashbuckle

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → Stack](../../README.md#stack) · lessons `[L01]` · `Api/OpenApi`

## Context

Swashbuckle has been the default OpenAPI/Swagger stack for ASP.NET Core for years. On **.NET 10** it
breaks: `Swashbuckle.AspNetCore` 7.x throws `TypeLoadException` on `GetSwagger` at runtime — it doesn't
support the target framework. A documented, browsable API surface is non-negotiable for this project, so
an alternative was needed rather than pinning to an older runtime.

## Decision

Generate the OpenAPI document with the first-party **`Microsoft.AspNetCore.OpenApi`** (`AddOpenApi` /
`MapOpenApi`) and render the interactive UI with **Scalar** (`MapScalarApiReference`, served at
`/scalar/v1`). Document customisation (security schemes, examples, per-version documents) is done with
**document/operation transformers** against the new `Microsoft.OpenApi` 2.0 object model.

## Consequences

- The API browser is at `/scalar/v1`, not `/swagger`; `launchSettings.json` and `.vscode/launch.json`
  point there, and the F5 `serverReadyAction` opens it automatically.
- We adopt `Microsoft.OpenApi` **2.0** types (`IOpenApiSecurityScheme`,
  `OpenApiSecuritySchemeReference`) — a transformer written for the 1.x model would not compile.
- The OpenAPI UI is **Development-only**; production health is proven by the readiness probe, not by
  OpenAPI being reachable (see ADR&nbsp;[0003] and `[L09]`).
- Transformers are written once and shared across every versioned document (ADR&nbsp;[0007]).

[0003]: 0003-uniform-error-contract-problemdetails.md
[0007]: 0007-api-versioning-by-url-segment.md
