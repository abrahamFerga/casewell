# Running and testing Casewell

Everything an agent (or a human) needs to take Casewell from a cold clone to a **proven** change.
Nothing here requires an API key, a cloud account, or a Cortex checkout.

Casewell is a **thin product host on the Cortex platform**: the security spine (auth,
multi-tenancy, RBAC-before-the-model, approvals, audit, jobs, chat transports, documents, RAG)
comes from the `Cortex.*` packages in [`.packages/`](.packages). This repo owns the `legal`
module — the domain — and nothing else.

> This file is the product's source of truth for how it runs. Every value below was read off a
> running instance, not assumed. [`OPERATIONS.md`](OPERATIONS.md) is a *deployment* document and
> describes a projected Azure layout, not this repo's local topology — do not use it to run this.

## 0. The one-screen version

```bash
dotnet run --project src/Casewell.AppHost      # run it
dotnet test Casewell.slnx                      # prove it
```

If both are green **and you exercised the change through the API or the UI**, you are done.
If you only ran `dotnet build`, you are not.

## 1. Prerequisites

| Need | Why | Check |
|---|---|---|
| **.NET 10 SDK** | everything targets `net10.0` (`global.json` pins `10.0.100`) | `dotnet --version` |
| **Docker Desktop, running** | Postgres + Redis under Aspire; Testcontainers for rungs 3–4 | `docker ps` |
| **pnpm / Node 20+** | *only* to run the SPA dev servers, and only with a sibling Cortex checkout | `corepack enable && pnpm -v` |

No AI key. The assistant runs on Cortex's dependency-free **`Mock` provider**, which streams
deterministic replies and performs **real, audited tool calls including the approval gate**. That
is what makes the whole security pipeline testable in CI and on a fresh clone. A real provider is a
per-tenant runtime setting (Admin → AI Settings) or an AppHost parameter — never a committed key.

## 2. Run

### Mode A — Aspire AppHost (the default)

```bash
dotnet run --project src/Casewell.AppHost
```

Brings up, per [`AppHost.cs`](src/Casewell.AppHost/AppHost.cs):

| Resource | What |
|---|---|
| `cortex-pg` | `pgvector/pgvector:pg17`, **with a data volume**, password parameter `cortex-pg-password` (dev default `casewell-dev-only`) |
| `cortex-platform`, `cortex-audit` | the two databases on it |
| `cortex-redis` | Redis |
| `casewell-api` | this host — take its external HTTP endpoint from the dashboard; that base URL is what every call below targets |
| `casewell-ui`, `casewell-admin-ui` | Vite dev servers, **only** if a sibling Cortex checkout exists and `pnpm` is on PATH |

The UI ships from the `@cortex/*` frontend packages. Until they publish to npm the AppHost launches
them from a sibling checkout (default `../Cortex`; override with `CortexRepoPath`). **No checkout →
the API still runs and the UI resources are skipped**, with a console line saying so. That is
normal, not a failure.

**`dotnet run` and `aspire run` are not equivalent.** They start the same stack, but an AppHost
launched with `dotnet run` is **invisible to the Aspire MCP** — the agent-readable observability
path. If you intend to read logs or traces through tooling, use `aspire run`. See §5.

### Mode B — headless (scripted verification, CI, no dashboard)

```powershell
dotnet build Casewell.slnx
docker rm -f casewell-pg-test 2>$null
docker run -d --name casewell-pg-test -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=cortex_platform `
  -p 5433:5432 pgvector/pgvector:pg17

$bin = "src/Casewell.Host/bin/Debug/net10.0"
$cs  = "Host=127.0.0.1;Port=5433;Database=cortex_platform;Username=postgres;Password=postgres"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$api = Start-Process dotnet -WorkingDirectory $bin -PassThru -ArgumentList @(
  "$PWD/$bin/Casewell.Host.dll",
  "--ConnectionStrings:cortex-platform=$cs",
  "--ConnectionStrings:cortex-audit=$cs",
  "--Ai:Provider=Mock",
  "--urls=http://127.0.0.1:8094")

1..45 | ForEach-Object { Start-Sleep 2; try { if ((iwr http://127.0.0.1:8094/alive -UseBasicParsing).StatusCode -eq 200) { "ready"; break } } catch {} }

# ... exercise it (§4) ...

Stop-Process -Id $api.Id -Force; docker rm -f casewell-pg-test
```

Boots in about **two seconds** once Postgres is accepting connections.

> **The two gotchas that will cost you an hour, both specific to this repo:**
>
> 1. **You must pass `--Ai:Provider=Mock`.** `appsettings.json` pins `Ai:Provider` to `None` and
>    **there is no `appsettings.Development.json`** — only the AppHost supplies `Mock`, via the
>    `ai-provider` parameter. Miss this and every turn answers
>    `RUN_ERROR "AI provider is not configured"`.
> 2. **Connection strings exist nowhere in config.** They arrive only from Aspire `WithReference`,
>    so a standalone `dotnet run --project src/Casewell.Host` cannot boot usefully. Supply
>    `cortex-platform` and `cortex-audit` yourself, as above.
>
> `-WorkingDirectory $bin` still matters so ContentRoot finds `appsettings.json`.

**Redis is not required to boot.** The AppHost wires `cortex-redis`, but the host starts and serves
every route above without it — verified. Postgres is the hard dependency.

There is **no `docker-compose.yml`** in this repo, so there is no released-image mode to verify
against here.

### Ready signals

| Signal | Meaning |
|---|---|
| `GET /alive` → `200 Healthy` | process is up. **Never calls the LLM** — safe to poll |
| `GET /health` → `200 Healthy` | dependencies reachable |
| `GET /api/platform/modules` contains `legal` | the module loaded and its manifest parsed |

## 3. Dev authentication

With no IdP configured (`Auth:Authority` empty) and `ASPNETCORE_ENVIRONMENT=Development`, the
platform's dev-header scheme is active. Send these on **every** call:

```http
X-Dev-Subject: dev-user
X-Dev-Tenant:  dev
X-Dev-Roles:   system_admin
```

`X-Dev-Email` and `X-Dev-Name` also exist. `system_admin` resolves to the `*` permission — confirm
with `GET /api/platform/me`. To test RBAC, send a **narrower** role and assert the 403; that is the
point of the header being per-request.

## 4. Exercise it

### The committed request catalog

[`casewell.http`](casewell.http) is the canonical, runnable list of every endpoint, with dev-auth
headers and no token setup. **When you add an endpoint, add its request there in the same PR.**

### A chat turn over AG-UI (the core feature)

```powershell
$h = @{ "X-Dev-Subject"="dev-user"; "X-Dev-Tenant"="dev"; "X-Dev-Roles"="system_admin" }
$body = @{ messages = @(@{ id="m1"; role="user"; content="List my matters" }) } | ConvertTo-Json -Depth 5
$r = Invoke-WebRequest "$base/api/agui/legal" -Method Post -Headers $h `
       -ContentType "application/json" -Body $body -UseBasicParsing
($r.Content -split "`n") | Where-Object { $_ -like "data:*" }
```

A healthy read-only turn streams exactly this:

```text
RUN_STARTED → TOOL_CALL_START → TOOL_CALL_END → TEXT_MESSAGE_START →
TEXT_MESSAGE_CONTENT (many) → CUSTOM(token_usage) → TEXT_MESSAGE_END → RUN_FINISHED
```

An **approval-gated** tool emits `CUSTOM {"name":"approval_required","value":{"toolName":"…"}}`
and the reply says the action *"requires human approval … was NOT executed"*. It must never claim
the write happened.

`RUN_ERROR` is always a failure, never noise:

| `RUN_ERROR` | Cause |
|---|---|
| `"AI provider is not configured"` | missing `--Ai:Provider=Mock` (Mode B gotcha 1) |
| `"Unknown module"` | the module id must be `legal` |

SignalR (`/hubs/agent`) is the other transport and goes through the *same* authorized, audited
runner — verifying one verifies the pipeline.

### The approval round trip

```powershell
Invoke-RestMethod "$base/api/chat/approvals" -Headers $h                       # pending
Invoke-RestMethod "$base/api/chat/approvals/$id/approve" -Method Post -Headers $h -Body "{}" -ContentType "application/json"
Invoke-RestMethod "$base/api/chat/approvals/$id/reject"  -Method Post -Headers $h -Body "{}" -ContentType "application/json"
```

Approving answers `{ "status": "Executed", "result": "<the tool's own output>" }` and drains the
call from the queue. Rejecting answers `Rejected` and writes nothing.

### Admin, RBAC, audit, usage

```powershell
Invoke-RestMethod "$base/api/platform/modules"       -Headers $h   # modules + tabs the caller sees
Invoke-RestMethod "$base/api/platform/me"            -Headers $h   # user, tenant, permissions
Invoke-RestMethod "$base/api/admin/security/catalog" -Headers $h   # every tool + its permission
Invoke-RestMethod "$base/api/admin/roles"            -Headers $h
Invoke-RestMethod "$base/api/admin/users"            -Headers $h
Invoke-RestMethod "$base/api/admin/audit/tool-calls" -Headers $h   # every invocation, append-only
```

After adding a tool, `security/catalog` **must** list it with its permission. If it does not, the
manifest and the tool source disagree and the tool will never be callable. Admin routes need
`platform.audit.view`; approvals need `chat.approvals.manage`.

## 5. Observe

**Aspire dashboard** — console logs, structured logs, traces, metrics per resource. First place to
look when a request misbehaves: the trace shows the tool call, the approval interception, and the DB
round-trips in one timeline.

**Aspire MCP / CLI** — the agent-readable view of the same OpenTelemetry (`list_resources`,
`list_console_logs`, `list_structured_logs`). Known reasons it reports *"No Aspire AppHost is
currently running"*:

| Cause | Fix |
|---|---|
| AppHost started with `dotnet run` | relaunch with **`aspire run`** — only the CLI opens the backchannel |
| CLI and AppHost SDK versions differ | update the CLI |
| Stale zero-byte `~/.aspire/cli/backchannels/aux.sock.*` | delete them |
| Just started | discovery is push-based; wait a few seconds |

In a headless or cron run the dashboard and MCP may be unavailable — use Mode B and read stdout.

## 6. The test ladder

Climb only as far as the change requires, but **never skip the rung that would catch your bug.**

| Rung | What it proves | Command |
|---|---|---|
| **1. Build** | it compiles | `dotnet build Casewell.slnx` |
| **2. Unit / module** | domain logic and the manifest surface guards | `dotnet test tests/Casewell.Legal.Tests` |
| **3. Integration (E2E)** | the real host, real Postgres, real migrations, real RBAC, real approvals | `dotnet test tests/Casewell.IntegrationTests` |
| **4. Golden evals** | agent *behaviour*: routing, gating, protocol | part of rung 3 (`Evals/cases/*.json`) |
| **5. Frontend** | n/a — the UI ships from the `@cortex/*` packages, not this repo | — |

Everything at once, exactly as CI runs it:

```bash
dotnet build Casewell.slnx -c Release
dotnet test  Casewell.slnx -c Release
```

**Without Docker**, rungs 3 and 4 cannot run. Skip them *explicitly* and say so in your report:

```bash
dotnet test Casewell.slnx --filter "FullyQualifiedName!~IntegrationTests"
```

**Keep the Postgres major consistent.** The AppHost and the test fixture both pin
`pgvector/pgvector:pg17`. A product that runs on pg17 but tests on pg16 is testing something it
does not ship.

### Rung 3 — how the E2E host is built

[`tests/Casewell.IntegrationTests/IntegrationFixture.cs`](tests/Casewell.IntegrationTests/IntegrationFixture.cs)
boots the **real** `Casewell.Host` via `WebApplicationFactory<Program>` against a **Testcontainers**
Postgres. Platform *and* legal migrations run, the dev tenant seeds, hosted services start. The
**Mock AI provider is the only stand-in**.

No Cortex package ships a test fixture, so this one is hand-written against three seams: the
dev-auth headers, `Ai:Provider=Mock`, and the `cortex-platform` / `cortex-audit` connection names.

**`fixture.AdminClient(roles, subject)`** is the entry point: an `HttpClient` carrying the dev-auth
headers. Everything security-shaped — RBAC 403s, the AG-UI stream, the approval gate — must be
proven through it, because it is the only path that goes through the real pipeline. Pass a narrower
role to assert a 403.

Seed domain fixtures through the module's hand-edit endpoints (`POST /api/legal/matters`), not
through the approval-gated chat tools — otherwise every fixture row needs an approval.

#### The approval-gate test is mandatory

[`ChatAndApprovalTests`](tests/Casewell.IntegrationTests/ChatAndApprovalTests.cs) covers the full
round trip over HTTP: the gate fires and the reply disclaims the write · the call is queued ·
approving executes it · the queue drains · rejecting discards it · a narrow role is refused.

Asserting `RequiresApproval = true` on a descriptor is a *static* check: it proves the flag is set,
not that the gate fires. Without this test the product can ship a broken human-in-the-loop gate
with fully green CI — the worst failure available on this platform.

### Rung 4 — golden conversation evals

One JSON file per case in
[`tests/Casewell.IntegrationTests/Evals/cases/`](tests/Casewell.IntegrationTests/Evals/cases).
Unknown fields fail loudly, so a typo surfaces instead of silently passing. Each case also
implicitly asserts `RUN_STARTED` + `RUN_FINISHED` present and `RUN_ERROR` absent.

```jsonc
{
  "name": "legal-trust-write-requires-approval",
  "module": "legal",
  "message": "Record a 5000 retainer deposit into trust on Eval Trust Co",
  "role": "system_admin",
  "seedMatters": ["Eval Trust Co"],
  "expectToolCalls": ["record_trust_deposit"],
  "forbidToolCalls": [],
  "expectApproval": true,
  "replyMustContain": ["approval", "NOT executed"],
  "replyMustNotContain": ["Recorded trust deposit"]
}
```

Add or adjust a case when you change:

| Change | Assert |
|---|---|
| a tool name or `[Description]` | the intent still routes there (`expectToolCalls`) |
| a `RequiresApproval` flag | `expectApproval` **and** the reply doesn't claim success |
| `AgentInstructions` | the reply reflects the policy (`replyMustContain`) |
| RBAC baselines | a narrower `role` + `forbidToolCalls` |

Limit: the Mock provider selects tools by name-token match and fills arguments naively, so evals
prove the **platform contract** (routing, gating, protocol) — not real-model reasoning quality.

## 7. The verification loop

A change is not done when it compiles. It is done when a test that **fails without it** passes
with it.

1. **Reproduce** — drive the failure through the narrowest surface that still shows it: a `.http`
   request, an AG-UI turn, a UI click. Write down the exact input and the exact wrong output.
2. **Observe** — read the trace/logs (§5), not the source. Find the first place reality diverges.
3. **Diagnose** — state the cause in one sentence. If you can't, you are still at step 2.
4. **Fix** — the smallest change that addresses that cause.
5. **Lock in** — add the test at the **lowest rung that would have caught it**, and run it against
   the *unfixed* code first. A regression test never seen red is not a regression test.
6. **Re-run the ladder** to the rung the change touches.

Escalate instead of looping if the same rung fails three times for different reasons — that means
the diagnosis is wrong, not the fix.

## 8. Gotchas

| Symptom | Cause / fix |
|---|---|
| `RUN_ERROR "AI provider is not configured"` | `appsettings.json` pins `Ai:Provider=None` and there is no Development override — pass `--Ai:Provider=Mock` |
| Host won't boot standalone | connection strings live only in the AppHost — supply `cortex-platform` and `cortex-audit` (Mode B) |
| `RUN_ERROR "Unknown module"` | the module id is `legal` |
| **A write isn't gated even though the manifest says it is** | `RequiresApproval` exists on **both** `ToolDescriptor` (manifest) and `ModuleTool` (tool source), and the gate is the **union** — either one alone gates the tool. Verified by removing each in turn: the gate only disappears when **both** are false. Set both, and assert the gate over HTTP, not on the flag |
| New tool never called, no error | it's missing from the manifest **or** from `IModuleToolSource` — both are required, and `security/catalog` shows the gap |
| Tool 403s for `system_admin` | the permission strings disagree — use `Permissions.ForTool(Id, "name")` in both places |
| Migration fails on the `vector` type | the image must be **pgvector**, not stock `postgres` |
| Aspire: containers up, API never starts, stack "hangs" after the banner | stale Postgres **data volume** initialized with a different password; `docker logs` shows `password authentication failed` and `WaitFor` blocks forever. `docker volume ls`, then `docker volume rm <name>`. Dev data is throwaway |
| `DLL is locked by .NET Host` on rebuild | a previous API process is still running — stop it first |
| Admin usage endpoints empty | token usage only exists after at least one chat turn |
| UI resources skipped at startup | no sibling Cortex checkout or no `pnpm` — expected; the API runs regardless |
| Port already in use | change `--urls` (Mode B) or stop the stale process |

## 9. CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) gates every PR: restore → Release build →
`dotnet test Casewell.slnx -c Release --no-build`. Cortex packages restore hermetically from the
committed [`.packages/`](.packages) feed.

`dotnet test` on the solution runs **every** test project, so rungs 3 and 4 now run in CI too and
require Docker on the runner (GitHub's `ubuntu-latest` provides it, and pulls
`pgvector/pgvector:pg17`).

Green CI is the floor, not the proof. CI cannot tell you the feature does what was asked — only §7
can.
