# Architectural Decision Records

Per [`formats/adr.md`](https://github.com/abrahamfernandez/the-workflow/blob/main/formats/adr.md) in TheWorkflow plugin. ADRs are append-only; supersession adds a new ADR rather than editing the old one.

---

## ADR-0001: Use Hangfire for background jobs instead of Quartz.NET

- **Status**: accepted
- **Date**: 2026-05-20
- **Deciders**: Abraham Fernandez

### Context

`GUARDRAILS.md` §2 requires a single in-process background-job scheduler. TheWorkflow's `dotnet-aspire-base` skill defaults to Quartz.NET. For Casewell, the Firm Admin persona has visibility into running jobs as a real value-add: trust-reconciliation reminders, invoice-send batches, GDPR-export progress, document-indexing jobs, connector dispatch — Firm Admins want to see what's queued, retry what failed, and pause what's noisy.

Quartz.NET has no built-in dashboard. Hangfire ships a dashboard out of the box (`/hangfire` route), with retry, pause, and inspection actions.

### Decision

We will use **Hangfire** (with the `Hangfire.PostgreSql` storage provider) for all background jobs in Casewell. The dashboard is exposed at `/hangfire`, gated by the `Connectors.Manage` policy (Firm Admin role).

### Consequences

- **Positive**: Out-of-the-box dashboard. Durable jobs survive container restarts. One fewer custom UI to build for the Firm Admin persona.
- **Negative**: One additional Postgres schema to manage (`hangfire.*`). Hangfire's commercial features are paid (Hangfire Pro for batches/continuations); we accept staying on the free tier.
- **Neutral**: A developer who knows Quartz will need a brief orientation; Hangfire's API is well-documented.

### Alternatives considered

- **Quartz.NET (the `dotnet-aspire-base` default)** — Rejected. No built-in dashboard, and building one for Firm Admins would duplicate work Hangfire already solves.
- **Azure Service Bus + Azure Functions** — Rejected for v1. Couples background work to a specific cloud (violates the `Infrastructure.<Cloud>` partition rule) and requires a separate compute container. We may revisit when load justifies.

---

## ADR-0002: Postgres-backed outbox instead of Azure Service Bus

- **Status**: accepted
- **Date**: 2026-05-20
- **Deciders**: Abraham Fernandez

### Context

`GUARDRAILS.md` §2 requires the outbox pattern for every domain event that triggers an external side effect. The default implementation choices are (a) a Postgres-backed outbox table polled by a worker, (b) a managed broker like Azure Service Bus or AWS SNS+SQS, (c) NATS / RabbitMQ.

For a 5–25 attorney firm, the outbox throughput is small: invoice-send events, connector dispatches, GDPR exports — single-digits per minute peak.

### Decision

Implement the outbox as a per-tenant Postgres table (`outbox_messages`), dispatched by a Hangfire recurring job (`OutboxDispatcherJob`, every 30 seconds) using `SELECT ... FOR UPDATE SKIP LOCKED` for safe concurrent claim.

### Consequences

- **Positive**: One dependency (Postgres) instead of two (Postgres + broker). Simpler local dev (Aspire just spins up Postgres). Transactional outbox guaranteed — the domain write and the outbox insert happen in the same EF Core transaction.
- **Negative**: 30-second polling latency for outbound messages (acceptable for invoice send; not acceptable for real-time chat — that's handled inline by the API, not via the outbox).
- **Neutral**: Migration to Service Bus when load justifies is a clean `IOutbox` swap; the abstraction in `Application` doesn't change.

### Alternatives considered

- **Azure Service Bus** — Rejected for v1. Two dependencies, more complex local dev, no real benefit at SMB scale.
- **NATS / RabbitMQ** — Rejected. Operating a broker is non-trivial; we'd rather buy Service Bus when we need it than self-host either of these.

---

## ADR-0003: Single Azure AD B2C tenant for staff and clients

- **Status**: accepted
- **Date**: 2026-05-20
- **Deciders**: Abraham Fernandez

### Context

`GUARDRAILS.md` §2 requires OIDC AuthN via Entra ID on Azure. We have two distinct user populations: firm staff (attorney/paralegal/firm-admin/bookkeeper) and clients (read-only portal access). Options: (a) one B2C tenant for both, with a `role` custom claim; (b) Entra ID for staff + a separate IdP for clients; (c) two B2C tenants.

### Decision

**Single Azure AD B2C tenant** for both staff and clients. Custom claims: `tenant_id` (the firm), `role` (one of attorney/paralegal/firm-admin/bookkeeper/client).

### Consequences

- **Positive**: One IdP to integrate, one set of redirect URIs, one sign-in flow code path. Cheaper (B2C pricing is per-MAU; one tenant is more efficient than two).
- **Negative**: Staff and client sign-up flows share a tenant — we mitigate by having two distinct user flows (`B2C_1_staff_signin`, `B2C_1_client_signin`) with different branding.
- **Neutral**: If we later need Entra ID for SSO with enterprise firms (Litify-style), it can be added as a federated IdP on the same B2C tenant.

### Alternatives considered

- **Entra ID for staff + Auth0 / Cognito / B2C for clients** — Rejected. Two IdPs is twice the integration cost; the role-claim approach gets us the separation we need.
- **Two separate B2C tenants** — Rejected. Doubles the cost and the maintenance; no real benefit when one tenant + user-flows accomplishes the same separation.

---

## ADR-0004: pgvector instead of Azure AI Search for AI assistant vector storage

- **Status**: accepted
- **Date**: 2026-05-20
- **Deciders**: Abraham Fernandez

### Context

The AI Matter Assistant epic requires semantic search over matter documents (for grounding chatbot responses). Options: (a) Postgres `pgvector` extension (same DB as operational data), (b) Azure AI Search (managed, hybrid search), (c) Pinecone / Weaviate / Qdrant (best-of-breed managed vector stores).

### Decision

Use **`pgvector` in the same Postgres instance**, behind an `IVectorStore` abstraction in `Application`.

### Consequences

- **Positive**: Zero additional infrastructure. Single backup boundary. Embeddings stored alongside the matter — no cross-system consistency window. `IVectorStore` abstraction keeps a future swap to Azure AI Search clean.
- **Negative**: Postgres + pgvector scales to roughly 1M-10M vectors per index comfortably; beyond that we'd want to migrate. For a 5–25 attorney firm with 1k-10k matters and a few thousand docs per matter, we're nowhere near that limit.
- **Neutral**: pgvector indexes (IVFFlat / HNSW) need tuning at moderate scale; v1 starts with IVFFlat default and we re-tune as needed.

### Alternatives considered

- **Azure AI Search** — Rejected for v1. Pays for managed hybrid (vector + BM25) search we don't need yet. Adds a second data store to keep consistent.
- **Pinecone / Weaviate / Qdrant** — Rejected. Best-in-class but over-engineered for our scale and add a third-party SaaS dependency.

---

## ADR-0005: Per-matter SAS tokens for document storage (5-minute expiry)

- **Status**: accepted
- **Date**: 2026-05-20
- **Deciders**: Abraham Fernandez

### Context

Documents live in Azure Blob Storage behind a private endpoint. The SPA and the chatbot need to fetch and display documents; the API needs to grant access without proxying every byte. Options: (a) per-matter SAS tokens issued by the API on demand, (b) per-tenant container-scope SAS, (c) API proxies all document I/O.

### Decision

The API issues a **per-matter, per-action SAS token** (5-minute expiry, scoped to the matter's blob folder) on demand for both read and write. The SPA uses the token to interact with Blob directly.

### Consequences

- **Positive**: Smallest blast radius if a token leaks (5 minutes, one matter, one action). Audit log captures every issuance with `actor + matter + action + expiry`. No proxy bandwidth on the API container.
- **Negative**: Clock-skew sensitivity — if the client clock is more than ~5 minutes off, the token appears expired. We mitigate by syncing the issued-at clock from the API response.
- **Neutral**: Token issuance is a small additional API hop per document interaction. Negligible at SMB scale.

### Alternatives considered

- **Per-tenant container-scope SAS** — Rejected. A leaked token would expose every matter in the firm; doesn't satisfy the per-matter access-control story we want for ABA Rule 1.6.
- **API proxies all bytes** — Rejected. Bandwidth + CPU cost on the API container, doubled by every chatbot context-fetch.

---

## ADR-0006: Hash-chained conflict attestations

- **Status**: accepted
- **Date**: 2026-05-20
- **Deciders**: Abraham Fernandez

### Context

ABA Rules 1.7 / 1.9 / 1.10 require conflict checks before opening a new matter. Most competitors implement this as a checkbox attestation — but the resulting record is not tamper-evident. Our differentiator is producing an audit-grade conflict-check record per `SPEC.md` differentiator #2.

### Decision

Each `ConflictAttestation` row records:
- `AttestationHash` — `SHA-256(DataSnapshotJson || PriorAttestationHash || PerformedAt || AttestedByUserId)`
- `PriorAttestationHash` — the hash of the most-recent attestation for the same matter (null for the first)
- `DataSnapshotJson` — the actual contact + party + opposing-party data the attestation was made against at that moment

This is an append-only hash chain per matter. Detection of tampering reduces to recomputing the hash of any row and comparing.

### Consequences

- **Positive**: Tamper-evidence is provable in audit (or in court). Competitive differentiator. Cheap to compute.
- **Negative**: Slightly larger row size (the snapshot JSON). Reasonable for the volume — one row per matter open, maybe one per conflict re-check.
- **Neutral**: Doesn't replace the underlying conflict-search logic; just records what was searched and when.

### Alternatives considered

- **Plain attestation record** — Rejected. Standard practice in the market, but doesn't differentiate and isn't audit-grade.
- **External notarisation (OpenTimestamps / Chainpoint)** — Considered but over-engineered for v1. Could be added later as a tamper-evidence boost on top of the hash chain.

---

## ADR-0007: Adopt the Cortex platform; retire the hand-rolled foundation

- **Status**: accepted — **supersedes ADR-0001 and ADR-0002; narrows ADR-0003 and ADR-0004**
- **Date**: 2026-07-02
- **Deciders**: Abraham Fernandez

### Context

The product owner directed: *"It needs to use Cortex completely… Remove anything that is not aligned with Cortex."* Cortex is the in-house multi-tenant agent platform (.NET 10 + Aspire + MAF, shipped as NuGet/npm packages) that already provides — tested and running — authentication (dev headers + external-IdP token mode), multi-tenancy, runtime-editable RBAC, append-only audit, AG-UI/SignalR chat, document/PDF tools, hybrid-search RAG with fail-closed collection gates, a leased background-job runner, a connector SDK (per-tenant enablement, protected secrets, per-user OAuth, sync), and two React front-ends (domain UI + admin console).

The first development iterations of TheLawyer (PR #25, branch feat/12) were re-implementing that same foundation by hand: own JWT/B2C wiring, own tenancy filters, own audit schema, own outbox + Hangfire, own React app. That is duplicated, divergent, and untested relative to the platform.

### Decision

TheLawyer is a **thin product host on Cortex packages**. This repo contains only:
1. `TheLawyer.Legal` — the legal domain as a Cortex module (`IModule`),
2. `TheLawyer.Host` — `AddCortexPlatform()` + the module + the product's connectors,
3. `TheLawyer.AppHost` — Aspire orchestration.

All hand-rolled platform code was removed. Platform gaps discovered while building legal features are fixed **in Cortex**, not re-implemented here.

### Consequences

- **Positive**: The Foundations epic collapses to composition; the platform's ~330-test suite covers what this repo no longer implements. Legal features (calendar, billing, trust, portal) build as module capabilities with RBAC/audit/RAG for free. Cross-vertical connection via `cortex-peer` comes free.
- **Negative**: Coupled to Cortex release cadence; until Cortex publishes to a package registry, the repo consumes a locally packed `.packages` feed (regenerated via `dotnet pack` from the Cortex repo — versions must be bumped on every repack to defeat the NuGet global cache).
- **Supersessions**: ADR-0001 (Hangfire) and ADR-0002 (hand-rolled outbox) are superseded by the Cortex job runner (leases, cooperative cancel, retries). ADR-0003 (B2C) survives narrowed: identity integrates through Cortex's `Auth:PermissionSource=Token` mode rather than bespoke wiring. ADR-0004 (pgvector) survives as-is — Cortex's RAG core is pgvector-based, now with hybrid tsvector+RRF search. ADR-0005/0006 remain product decisions to realize inside the module.

### Alternatives considered

- **Continue the hand-rolled foundation** — Rejected by the product owner; duplicates the platform and diverges from it.
- **Fork Cortex code into this repo** — Rejected. Package consumption keeps one platform, one fix-location, many products.
