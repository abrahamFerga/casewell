# Casewell — Architecture

> **Realigned 2026-07-02** (see [ADR-0007](DECISIONS.md#adr-0007-adopt-the-cortex-platform-retire-the-hand-rolled-foundation)):
> Casewell is a **thin product host on the Cortex platform**. Cortex — a
> multi-tenant, module-based agent platform (.NET 10 + Aspire + MAF) — supplies
> authentication/authorization, multi-tenancy, RBAC, append-only audit, chat
> (AG-UI + SignalR), document/PDF tools, RAG, background jobs, connectors, and
> the React UI shell. This repo owns **only the legal domain** and the product
> composition. Anything below that describes platform behavior is documentation
> of what Cortex provides, not code in this repo.

## Context (C4 L1)

Diagram: [`docs/diagrams/c1-context.puml`](docs/diagrams/c1-context.puml)

Actors (unchanged by the realignment):
- **Attorney / Paralegal / Firm Admin / Bookkeeper** — staff users. Dev: header auth; production: external IdP (Entra External ID / B2C) via Cortex's `Auth:PermissionSource=Token` mode.
- **Client** — read-only portal role (same IdP, different role claim).
- **Microsoft Graph (Outlook/SharePoint/OneDrive)** — via the Cortex `msgraph` connector (per-user delegated OAuth).
- **Azure Blob Storage** — via the Cortex `azure-blob` connector.
- **Peer Cortex systems** (e.g. a finance vertical) — via the `cortex-peer` connector over AG-UI.
- **AI provider** — any MEAI `IChatClient` provider; `Mock` by default (keyless), OpenAI/AzureOpenAI/Ollama via configuration.

## Containers (C4 L2)

Diagram: [`docs/diagrams/c2-containers.puml`](docs/diagrams/c2-containers.puml)

| Container | Tech | Purpose |
|---|---|---|
| `Casewell.AppHost` | .NET 10 Aspire AppHost | Orchestration: pgvector Postgres (platform + audit DBs), Redis, the API. AI provider/model/key are AppHost parameters (default `Mock`). |
| `Casewell.Host` | ASP.NET Core + `Cortex.AspNetCore` | The product. `AddCortexPlatform()` + `AddCortexModule<LegalModule>()` + four connectors. All platform endpoints (chat, admin, connectors, jobs, RAG) come from the package. |
| `Casewell.Legal` | `Cortex.Modules.Sdk` module | The legal domain: matters, parties, ethical walls, clause library, playbook, bulk review, matter knowledge (RAG), folder sync. Own EF Core `LegalDbContext` + migrations. |
| Postgres | `pgvector/pgvector:pg17` | `cortex-platform` (platform + module schemas, embeddings) and `cortex-audit` (append-only audit) databases. |
| Redis | Redis 7 | Cache/backplane (platform-managed). |
| Web UI | `@cortex/ui` + `@cortex/admin-ui` (React) | Domain UI (chat with AG-UI transport, drag-drop uploads, module tabs) and admin console (security map, users/roles, usage, audit, connectors). Served from the Cortex frontend packages; until they publish to npm, run from the Cortex repo with `VITE_API_BASE` pointed at this API. |

## Components (C4 L3) — what lives where

Diagram: [`docs/diagrams/c3-components-api.puml`](docs/diagrams/c3-components-api.puml)

### Provided by Cortex packages (not in this repo)

- **AuthN/AuthZ** — dev header scheme (`X-Dev-*`, Development-only) or JWT bearer against an external IdP. `Auth:PermissionSource=Token` makes the IdP the source of truth, with role→permission baselines as a runtime-editable translation layer (per-tenant admin editor).
- **Multi-tenancy + RBAC** — tenant resolution, permission catalog (`tools.{module}.{tool}`, `tools.connectors.{id}.{tool}`), `AuthorizedAgentRunner` enforcing per-tool permissions on every agent run, tool-call audit.
- **Chat** — AG-UI protocol endpoint `POST /api/agui/{moduleId}` (SSE) and a SignalR hub; both authorized + audited. Streaming, token-usage events, approval-required events.
- **Documents & files** — platform file store, PDF read/generate tools (PdfPig), upload endpoints used by drag-drop.
- **RAG** — `RagCollection`/`RagChunk`, hybrid pgvector + tsvector search (RRF), `platform.rag-ingest` job, `tools.knowledge.search_knowledge`, `IRagCollectionGate` (fail-closed) for per-collection access control.
- **Jobs** — leased job runner (lease expiry, cooperative cancel, max attempts, capability snapshot).
- **Connectors** — registry, per-tenant enable/disable (default OFF), DataProtection-protected secrets (write-only via admin API), per-user OAuth (PKCE), scoped bindings, sync jobs.
- **Admin API + console** — security catalog, users/roles, token usage, audit log, connector management.

### In this repo

- **`LegalModule`** (`IModule`, manifest v1.3.0) — 13 agent tools over matters, clause library, playbook, walls, bulk review; module tabs for the UI shell; `MatterRagGate` (an `IRagCollectionGate`: matter-scoped knowledge respects ethical walls); `MatterSyncHandler` (folder-sync → matter documents → RAG ingest).
- **`LegalDbContext`** — module-owned schema + 3 migrations (matters/parties, clause library/playbook, walls).
- **Host composition** (`Casewell.Host/Program.cs`):

```csharp
builder.AddCortexPlatform();
builder.AddCortexModule<LegalModule>();
builder.AddCortexConnector<LocalFolderConnector>();
builder.AddCortexConnector<AzureBlobConnector>();
builder.AddCortexConnector<MsGraphConnector>();
builder.AddCortexConnector<CortexPeerConnector>();
var app = builder.Build();
await app.RunCortexPlatformAsync();
```

## Solution layout

```
the-lawyer/
├── Casewell.slnx
├── nuget.config                      # local .packages feed for Cortex.* until they publish
├── src/
│   ├── Casewell.AppHost/            # Aspire: pgvector pg17, Redis, API; AI params (Mock default)
│   ├── Casewell.Host/               # AddCortexPlatform() + module + connectors
│   └── Casewell.Legal/              # the legal module (domain code only)
├── tests/
│   └── Casewell.Legal.Tests/        # module unit tests
├── research/ SPEC.md PLAN.md ARCH.md DECISIONS.md
└── docs/diagrams/                    # C4 PlantUML
```

## Cross-cutting wiring

All rows below are **platform behavior configured, not implemented, here**:

| Concern | How Casewell gets it |
|---|---|
| AuthN | Dev: `X-Dev-*` headers. Prod: external IdP JWT with `Auth:PermissionSource=Token` (Cortex translates IdP roles → permissions via editable baselines). |
| AuthZ | Cortex RBAC; legal tools surface as `tools.legal.*` permissions in the admin security catalog. |
| Multi-tenancy | Platform tenant context + per-tenant connector/RBAC state. |
| Audit | Append-only audit DB (`cortex-audit`); every agent tool call recorded. |
| Observability | Cortex ServiceDefaults (OTel traces/metrics/logs → Aspire dashboard locally). |
| Background work | Platform job runner (leases, cancel, retries) — used by bulk review and RAG ingest. |
| AI | MEAI provider factory; `Mock` provider + `MockEmbeddingGenerator` make the full stack keyless-testable. |
| Documents | Platform file store + PDF tools; matter documents flow through connector sync into matter RAG collections. |
| Vector search | pgvector + tsvector hybrid (RRF), matter-scoped collections gated by `MatterRagGate` (ethical walls). |

## Cloud topology

Azure remains the target (`workflow.json` cloud=azure): Container Apps for the host, Azure Database for PostgreSQL Flexible Server (pgvector), Azure Cache for Redis, Blob via the `azure-blob` connector, Key Vault for secrets, Entra External ID/B2C in token mode. Terraform/deployment lands with the operations epic; Aspire's model is the source of truth for topology.

## Data model

The platform owns identity/RBAC/audit/jobs/RAG/connector tables. The legal module owns (in `LegalDbContext`, EF migrations checked in):

- `Matter`, `Party` — matter workspace, conflict surface
- `MatterWall` — ethical walls (deny-list enforced in tools and in `MatterRagGate`)
- `ClauseLibraryEntry`, `PlaybookRule` — tenant clause library + drafting playbook
- `BulkReviewJob` rows ride the platform job runner
- Matter knowledge = platform `RagCollection` per matter (`legal.matter.{id}`)

## Agent surface

One module agent (`legal`) exposed over AG-UI at `/api/agui/legal`, with 13 domain tools plus platform tools (documents, knowledge search) and any enabled connector tools — all permission-filtered per user at run time by `AuthorizedAgentRunner`.

## Diagrams

- [`docs/diagrams/c1-context.puml`](docs/diagrams/c1-context.puml) — actors + system box
- [`docs/diagrams/c2-containers.puml`](docs/diagrams/c2-containers.puml) — Aspire containers + Cortex platform split
- [`docs/diagrams/c3-components-api.puml`](docs/diagrams/c3-components-api.puml) — platform-vs-module component split
