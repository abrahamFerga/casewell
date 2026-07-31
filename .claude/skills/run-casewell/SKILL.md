---
name: run-casewell
description: >
  Run, exercise, observe, and prove a change in Casewell — the legal product host on the Cortex
  platform. Covers the Aspire AppHost and headless launch modes, the dev-auth headers, the keyless
  Mock AI provider, the AG-UI event contract, the approval round trip, and the four-rung test
  ladder. USE FOR: starting the app, calling its API, reproducing a bug, deciding which tests a
  change needs, or confirming a change actually works. DO NOT USE FOR: deployment topology (see
  OPERATIONS.md) or authoring module code.
---

# Run Casewell

**[`RUNBOOK.md`](../../../RUNBOOK.md) is the source of truth.** This skill is the index that makes
it findable; read the runbook for anything below in depth.

## The 30-second version

```bash
dotnet run --project src/Casewell.AppHost      # run it (Aspire: Postgres, Redis, API, optional UI)
dotnet test Casewell.slnx                      # prove it (all four rungs)
```

Take the API base URL from the Aspire dashboard resource `casewell-api`. Docker must be running.
No API key is needed — the agent runs on Cortex's `Mock` provider, which still performs **real,
audited, approval-gated tool calls**.

## Dev auth — send on every call

```http
X-Dev-Subject: dev-user
X-Dev-Tenant:  dev
X-Dev-Roles:   system_admin
```

`system_admin` → the `*` permission. Send a **narrower** role to assert a 403.

## Ready signals

| Check | Meaning |
|---|---|
| `GET /alive` → 200 | up; never calls the LLM, safe to poll |
| `GET /health` → 200 | dependencies reachable |
| `GET /api/platform/modules` contains `legal` | the module loaded |

## Exercise it

[`casewell.http`](../../../casewell.http) is the runnable catalog of every endpoint. **Add a request
there whenever you add an endpoint.**

A chat turn is `POST /api/agui/legal`. A healthy read-only turn streams
`RUN_STARTED → TOOL_CALL_* → TEXT_MESSAGE_* → CUSTOM(token_usage) → RUN_FINISHED`, with **no**
`RUN_ERROR`. An approval-gated write instead emits `CUSTOM(approval_required)` and the reply must
not claim the write happened; `GET /api/chat/approvals` then lists it, and
`POST /api/chat/approvals/{id}/approve` executes it.

After adding a tool, `GET /api/admin/security/catalog` **must** list it with its permission —
otherwise the manifest and `IModuleToolSource` disagree and the tool is never callable.

## The test ladder

| Rung | Command |
|---|---|
| 1. Build | `dotnet build Casewell.slnx` |
| 2. Unit / module | `dotnet test tests/Casewell.Legal.Tests` |
| 3. Integration (real host + Testcontainers Postgres) | `dotnet test tests/Casewell.IntegrationTests` |
| 4. Golden evals | inside rung 3 (`Evals/cases/*.json`) |

Anything security-shaped — RBAC, the approval gate, the AG-UI protocol — must be proven through
`fixture.AdminClient()` over real HTTP, never by asserting a flag.

Without Docker, skip rungs 3–4 *explicitly* and say so:
`dotnet test Casewell.slnx --filter "FullyQualifiedName!~IntegrationTests"`.

## The three traps that cost the most time

1. **Headless runs need `--Ai:Provider=Mock`** — `appsettings.json` pins `None` and there is no
   Development override. Otherwise every turn is `RUN_ERROR "AI provider is not configured"`.
2. **Connection strings exist only in the AppHost** — supply `cortex-platform` and `cortex-audit`
   yourself when running the host directly (RUNBOOK.md §2, Mode B).
3. **`RequiresApproval` is a union of two flags** — the manifest's `ToolDescriptor` *and* the tool
   source's `ModuleTool`. Either alone gates the tool, so set both and assert the gate over HTTP.

A change is done when a test that **fails without it** passes with it — see RUNBOOK.md §7.
