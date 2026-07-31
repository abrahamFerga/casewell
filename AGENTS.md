# Casewell — agent instructions

Casewell is a **thin product host on the Cortex platform**: the security spine — auth,
multi-tenancy, RBAC-before-the-model, approvals, audit, jobs, chat transports, documents, RAG —
comes from the `Cortex.*` packages in [`.packages/`](.packages). This repo owns the **`legal`
module** (the domain) and nothing else.

The single most common way to waste a day here is to rebuild something the platform already
provides. Before writing code, check whether the seam already exists.

## Run and test

```bash
dotnet run --project src/Casewell.AppHost      # run it
dotnet test Casewell.slnx                      # prove it
```

Exactly as CI runs it:

```bash
dotnet build Casewell.slnx -c Release
dotnet test  Casewell.slnx -c Release
```

Without Docker, the integration and eval rungs cannot run. Skip them **explicitly** and say so:

```bash
dotnet test Casewell.slnx --filter "FullyQualifiedName!~IntegrationTests"
```

**[`RUNBOOK.md`](RUNBOOK.md) is the source of truth for running this product** — headless mode, dev
auth headers, the AG-UI event sequence, observability, and the full test ladder. Read it before
running anything; do not re-derive it. [`OPERATIONS.md`](OPERATIONS.md) describes a *projected Azure
deployment*, not this repo's local topology — do not use it to run this.

No API key is needed. The assistant runs on Cortex's `Mock` provider, which still performs real,
audited tool calls **including the approval gate**.

## Layout

| Path | What |
|---|---|
| `src/Casewell.AppHost` | Aspire orchestration — Postgres (pgvector), Redis, the API, the UIs |
| `src/Casewell.Host` | the thin host: composes the platform and registers the legal module |
| `src/Casewell.Legal` | **the domain** — tools, manifest, persistence, jobs |
| `src/Casewell.Legal/Persistence` | `LegalDbContext` — per-entity `HasQueryFilter` (tenant isolation) |
| `tests/Casewell.Legal.Tests` | unit / module tests |
| `tests/Casewell.IntegrationTests` | real host + real Postgres via Testcontainers, plus golden evals |
| [`casewell.http`](casewell.http) | the runnable catalog of every endpoint |
| `.packages/` | the committed Cortex feed — CI restores hermetically from it |

## Rules most often broken

These are the ones that cost hours. The rest are in [`RUNBOOK.md §8`](RUNBOOK.md).

1. **A tool must be declared in two places** — the manifest (`ToolDescriptor`) *and*
   `IModuleToolSource`. Missing either means the tool is never callable, with no error.
   `GET /api/admin/security/catalog` shows the gap.
2. **`RequiresApproval` is a union across both places.** Either one alone gates the tool, so a
   write can look gated on the descriptor and still not be. Set both, and assert the gate **over
   HTTP** — asserting the flag proves nothing.
3. **Use `Permissions.ForTool(Id, "name")` in both places.** Disagreeing permission strings 403 a
   `system_admin`.
4. **Every entity needs `HasQueryFilter`.** It is tenant isolation; omitting it leaks across
   tenants and no test will tell you unless you write one.
5. **When you add an endpoint, add its request to [`casewell.http`](casewell.http) in the same PR.**
6. **Seed fixtures through the hand-edit endpoints** (`POST /api/legal/matters`), not the
   approval-gated chat tools — otherwise every fixture row needs an approval.
7. **Keep the Postgres major at `pgvector/pgvector:pg17`** in both the AppHost and the test fixture.
   A product that ships on pg17 and tests on pg16 is testing something it does not ship.

## How work is judged

**A change is not done when it compiles. It is done when a test that fails without it passes with
it.** Green CI is the floor, not the proof — CI cannot tell you the feature does what was asked.

Climb the ladder only as far as the change requires, but never skip the rung that would catch your
bug. The rungs are in [`RUNBOOK.md §6`](RUNBOOK.md); the loop that uses them is §7.

State claims at the level you actually verified them:

| Level | Means |
|---|---|
| **L1** | an exit code or a captured output — the strongest claim available |
| **L2** | config is present and parses |
| **L3** | reasoned from source you read |
| **L4** | judgement — say so plainly |

Report one of four terminal states, named explicitly: **Success**, **No-op**, **Blocked**,
**Approval-required**.

### Pull requests

`.github/workflows/agent-gates.yml` runs [`pr-gates.mjs`](.github/scripts/pr-gates.mjs) as a
required check. On a `feat/*`, `fix/*` or `chore/*` branch the body **must** carry:

- `Closes #<n>`
- a `## Runtime evidence` section — the request you actually exercised and the output you observed
- a `## Regression test` section — the test seen **red before** the fix and green after

A `## Regression test` describing a test never seen red will pass the gate and is still a lie; the
gate checks the words, you supply the honesty.

### The spine

A diff that **removes or edits** a line touching `HasQueryFilter`, `RequiresApproval`,
`AddPlenipoRole`, a `Permissions.` string, `.github/`, `CODEOWNERS`, `nuget.config` or
`appsettings*.json` fails the spine gate and needs a human. Adding such a line is ordinary feature
work; changing one is a change to what the platform exists to guarantee. The override is the
`human-approved` label — a deliberate human act, recorded on the PR.

Merging is governed by `autonomy.level` in [`workflow.json`](workflow.json), which
[`merge-gate.mjs`](.github/scripts/merge-gate.mjs) reads and refuses to exceed. **Never raise it.**

## Deeper

| File | For |
|---|---|
| [`RUNBOOK.md`](RUNBOOK.md) | running, testing, observing, debugging — start here |
| [`ARCH.md`](ARCH.md) | architecture and the platform boundary |
| [`DECISIONS.md`](DECISIONS.md) | ADRs — why things are the way they are |
| [`SPEC.md`](SPEC.md) / [`PLAN.md`](PLAN.md) | what the product is and the build order |
| [`SECURITY.md`](SECURITY.md) | the security model |

**These instructions are advisory.** Every tool that reads this file may skim or ignore it. What is
actually enforced lives in `.github/workflows/` and the gate scripts — nothing in this file stops
anything on its own.
