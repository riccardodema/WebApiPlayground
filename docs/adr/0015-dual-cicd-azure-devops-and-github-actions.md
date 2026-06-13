# ADR 0015 — CI/CD implemented twice: Azure DevOps and GitHub Actions

- **Status:** Accepted · **Date:** 2026-06-13
- **See also:** [README → CI/CD](../../README.md#cicd--two-implementations) · `.azure/`, `.github/workflows/` · `.claude/context/cicd.md`

## Context

A delivery pipeline is part of the engineering, not an afterthought. Two ecosystems dominate .NET shops —
Azure DevOps and GitHub Actions — and the concepts (templates vs reusable workflows, variable groups vs
environments, service connections vs OIDC) map onto each other but aren't identical.

## Decision

Implement the **same pipeline twice**, once per platform, sharing logic internally (Azure DevOps
`templates/`, GitHub `workflow_call` reusable workflow). Both: build the DACPAC, **publish the DB schema
before deploying the app**, deploy to Azure App Service, then **health-check `/health/ready`**. The deploy
stage is gated by a manual approval (Azure *Environment* / GitHub *Environment*); GitHub auth to Azure
uses **OIDC federated credentials** (no long-lived secrets). The same *disabled-until-configured* pattern
as the app applies — the deploy job is **skipped, not failed**, until the Azure settings exist.

## Consequences

- Demonstrates portability across both CI systems and the DRY techniques each one offers.
- The post-deploy check targets the **readiness probe**, not `/openapi/v1.json` — which doesn't exist in
  production and would give a false green (`[L09]`).
- A required status check must run on **every** PR: `paths-ignore` on a required workflow deadlocks
  PRs that touch only ignored paths (the check is never reported) (`[L04]`).
- Runnable on the Azure free tier (App Service F1 + Azure SQL free). Live deployment is pending an Azure
  subscription — the pipelines are complete and gated, not yet exercised against a real environment.
