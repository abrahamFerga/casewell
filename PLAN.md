# Casewell — Plan

## Epics (in build order)

1. **Foundations** *(epic 1, always)* — auth (OIDC), multi-tenancy (row-level), observability (OpenTelemetry via Aspire), RBAC scaffold, dashboard shell (SPA shell + sidebar + topbar + tenant switch), chatbot panel placeholder, connector registry contracts, outbox infrastructure, audit-log infrastructure. Capabilities (from SPEC): foundations of all of them. Depends on: nothing.
2. **Matters & Intake** — open a new matter, capture parties, run conflict-check attestation, manage contacts. Capabilities: *Matter management*. Depends on: Foundations.
3. **Documents & Templates** — per-matter file store with versioning, secure sharing, Word-template merge. Capabilities: *Document management + automation*. Depends on: Foundations, Matters.
4. **Calendar & Deadlines** — matter-linked events, manual deadline entry, reminder rules. Capabilities: *Calendar with reminders*. Depends on: Foundations, Matters.
5. **Time & Billing** — time entries (lightweight in-context timer), invoice draft/approve/send, expenses, flat-fee handling, separation of duties on approval. Capabilities: *Billing & invoicing*. Depends on: Foundations, Matters.
6. **Trust Accounting** — IOLTA-compliant ledger with per-jurisdiction config, three-way reconciliation, API-layer hard guardrails. Capabilities: *Trust accounting*. Depends on: Foundations, Time & Billing.
7. **Client Portal** — read-only matter view, invoice payment, document signing. Capabilities: *Client portal*. Depends on: Foundations, Matters, Documents, Time & Billing.
8. **AI Matter Assistant** *(differentiator)* — Claude-powered chatbot scoped per matter; private endpoints, no-retention contract; UI integrated into the dashboard chatbot panel. Capabilities: *Matter-scoped AI assistant*. Depends on: Foundations, Matters, Documents.
9. **Connectors** *(differentiator)* — installation flow + per-tenant config for `slack`, `docusign`, `quickbooks`, `outlook`, `dropbox`. v1 ships `slack` as the working end-to-end connector; others are wired with their `connector.json` placeholders. Capabilities: *Pluggable channel connectors*. Depends on: Foundations.
10. **Reporting** — origination, realization, WIP, A/R aging, productivity by timekeeper, trust-reconciliation status. Capabilities: *Reporting & analytics* (success-metric instrumentation). Depends on: every prior epic that produces data.

## Module list

| Module (.NET project) | Bounded context | Capabilities served | Stack/pattern skills used to build it |
|---|---|---|---|
| `Casewell.AppHost` | (orchestration) | Aspire AppHost composing every resource | `dotnet-aspire-base` |
| `Casewell.ServiceDefaults` | (cross-cutting) | OTel + health checks + resilience | `dotnet-aspire-base` |
| `Casewell.Domain` | domain | Entities + value objects + domain events + PII attribute | `dotnet-aspire-base` |
| `Casewell.Application` | application core | Cross-cutting handlers, abstractions (`IAiAssistantService`, `ITenantContext`, `IAuditLog`, `IConflictAttestor`) | `dotnet-aspire-base`, `rbac` *(planned)*, `multi-tenant` *(planned)* |
| `Casewell.Application.Matters` | matters | Matter management, intake, conflict-check, contacts, parties | `rbac` *(planned)* |
| `Casewell.Application.Documents` | documents | Doc mgmt, Word-template merge | (none yet — custom) |
| `Casewell.Application.Calendar` | calendar | Events, deadlines, reminders | (none yet — custom) |
| `Casewell.Application.Billing` | billing | Time, invoices, expenses | (none yet — custom) |
| `Casewell.Application.Trust` | trust | IOLTA ledger with API-layer guardrails | (none yet — custom) |
| `Casewell.Application.Portal` | client portal | Client-facing read + pay + sign use-cases | (none yet — custom) |
| `Casewell.Application.AiAssistant` | ai | MAF-based matter chatbot via `IAiAssistantService` impl | `maf-agents` *(planned)*, `industry-chatbot` *(planned)* |
| `Casewell.Application.Connectors` | connectors | Connector registry, `IChannel` + `IIntegration` contracts | `pluggable-connectors` |
| `Casewell.Application.Reporting` | reporting | Read-models + queries for dashboards | (none yet — custom) |
| `Casewell.Infrastructure` | infrastructure | EF Core, outbox, multi-tenant query filters, audit log | `dotnet-aspire-base` |
| `Casewell.Infrastructure.Azure` | infrastructure (cloud) | Azure-specific: Key Vault, Blob, Service Bus, Entra ID | `azure-terraform` *(planned)* |
| `Casewell.Infrastructure.Slack` | infrastructure (connector) | Slack `IChannel` implementation | `pluggable-connectors` |
| `Casewell.Api` | api | Minimal APIs grouped by bounded context, versioned (`/api/v1/...`), Problem Details, idempotency keys, rate limiting | `dotnet-aspire-base`, `rbac` *(planned)* |
| `Casewell.Web` | web | Vite + React + TS + shadcn/ui + Tailwind + PWA | `react-vite-shadcn` *(planned)*, `dashboard-portal` *(planned)*, `industry-chatbot` *(planned)* |

Skills marked *(planned)* don't exist in TheWorkflow yet. The Foundations epic will be generated with custom code where the planned skills don't yet provide guidance; once those skills land in TheWorkflow, the affected files can be re-generated or refactored to match the patterns they encode.

## Data model sketch

Stay at the conceptual level — concrete EF entities and migrations come from `design-architecture`. PII fields marked with `[Pii]` are flagged here so the architecture's PII-tagging mechanism wires them automatically.

### Tenancy
- **`Tenant`** *(firm)* — `Id`, `Name`, `PrimaryState`, `IoltaConfigJson`, `CreatedAt`.
- **`User`** — `Id`, `TenantId`, `Email` *(PII)*, `FullName` *(PII)*, `Role`, `IdentityProviderSub`.

### Contacts & matters
- **`Contact`** *(person or entity)* — `Id`, `TenantId`, `Type`, `DisplayName` *(PII)*, `EmailJson` *(PII)*, `PhoneJson` *(PII)*, `AddressJson` *(PII)*.
- **`Matter`** — `Id`, `TenantId`, `MatterNumber`, `Title`, `PracticeArea`, `Status`, `OpenedAt`, `ClosedAt`, `PrimaryAttorneyId`, `ConflictCheckAttestationId`.
- **`Party`** *(party-to-matter join)* — `Id`, `TenantId`, `MatterId`, `ContactId`, `RoleInMatter`.
- **`ConflictAttestation`** — `Id`, `TenantId`, `MatterId`, `AttestedByUserId`, `PerformedAt`, `AttestationHash`, `PriorAttestationHash`, `DataSnapshotJson`. Hash-chained, append-only.

### Documents
- **`Document`** — `Id`, `TenantId`, `MatterId`, `Filename`, `StorageUri`, `Version`, `UploadedAt`, `UploadedByUserId`, `ContainsPii` *(boolean tag for matter-content PII)*.
- **`DocumentTemplate`** — `Id`, `TenantId`, `Name`, `WordTemplateUri`, `MergeFieldsJson`.

### Calendar
- **`CalendarEvent`** — `Id`, `TenantId`, `MatterId`, `Type`, `StartsAt`, `EndsAt`, `Title`, `Description`, `ReminderRuleJson`.

### Time, billing & expenses
- **`TimeEntry`** — `Id`, `TenantId`, `MatterId`, `UserId`, `DurationMinutes`, `ActivityCode`, `Narrative`, `Billable`, `BilledInvoiceId`.
- **`Expense`** — `Id`, `TenantId`, `MatterId`, `Amount`, `Description`, `IncurredOn`, `Billable`, `BilledInvoiceId`.
- **`Invoice`** — `Id`, `TenantId`, `MatterId`, `Status`, `GeneratedAt`, `ApprovedAt`, `SentAt`, `PaidAt`, `TotalAmount`, `ApprovedByUserId`.

### Trust accounting
- **`TrustAccount`** — `Id`, `TenantId`, `Jurisdiction`, `AccountNumberLast4`, `CurrentBalance`.
- **`TrustLedgerEntry`** — `Id`, `TenantId`, `TrustAccountId`, `MatterId`, `ContactId`, `PostedAt`, `Amount` *(signed)*, `EntryType`, `Description`, `ReconciliationId`.
- **`Reconciliation`** — `Id`, `TenantId`, `TrustAccountId`, `Period`, `BankBalance`, `BookBalance`, `Status`, `ReconciledAt`, `ReconciledByUserId`.

### Connectors
- **`InstalledConnector`** — `Id`, `TenantId`, `Name`, `Enabled`, `ConfigSecretRefJson`, `InstalledAt`.

### Cross-cutting
- **`OutboxMessage`** — `Id`, `TenantId`, `OccurredAt`, `AggregateType`, `AggregateId`, `EventType`, `PayloadJson`, `ProcessedAt`, `Attempts`.
- **`AuditEntry`** — separate `audit` schema. `Id`, `TenantId`, `OccurredAt`, `ActorUserId`, `ActorRole`, `Action`, `ResourceType`, `ResourceId`, `BeforeJson`, `AfterJson`, `IdempotencyKey`.

Multi-tenancy: `TenantId` on every row except `Tenant` itself. EF Core global query filter applied; the only way to bypass is via an explicit `IgnoreTenantFilter()` call (audit-logged when used).

## RBAC model (refined)

| Role | Policies | Notes |
|---|---|---|
| `attorney` | `Matters.View`, `Matters.Open`, `Matters.Edit` *(assigned only)*, `Time.Bill`, `Invoices.Draft` *(assigned matters)*, `Documents.Edit`, `Calendar.Edit`, `AiAssistant.Use` | Cannot approve own invoices (separation of duties via `Invoices.Approve` policy denying when `ApprovedByUserId == DraftedByUserId`). |
| `paralegal` | `Matters.View`, `Matters.Edit` *(assigned)*, `Documents.Edit`, `Calendar.Edit`, `Invoices.Draft`, `AiAssistant.Use` | No `Invoices.Approve`, no `Trust.Post`. |
| `firm-admin` | `Matters.*`, `Users.Manage`, `Connectors.Manage`, `Reports.View`, `Tenant.Configure`, `Invoices.Approve` | Cannot bill time as themselves on a matter they admin (rare edge; flagged at intake). |
| `bookkeeper` | `Trust.View`, `Trust.Post`, `Reconciliation.Manage`, `Invoices.View`, `Invoices.Approve`, `Reports.View` | No access to substantive matter content (read on `MatterFinancialSummary` projection only). |
| `client` | `Matter.Self.View`, `Invoice.Self.Pay`, `Documents.Self.Sign` | All `.Self.*` policies scope by `Matter.PartyContactId == CurrentUser.ContactId`. |

Policy names use `<Module>.<Action>` form. Code references the policy name via `[Authorize(Policy = "Matters.Open")]` attributes; the role-to-policy binding lives in `appsettings.json` under `Rbac:RoleAssignments` so industry-specific firms can customise without recompiling.

## Integration surface

| Connector | Direction | Purpose | Webhook route | Per-tenant config |
|---|---|---|---|---|
| `slack` | inbound + outbound | Chatbot fanout; matter notifications. **Working v1 connector.** | `/api/connectors/slack/webhook` | `bot_token_secret_ref`, `signing_secret_ref`, `default_channel` |
| `docusign` | outbound + callback | E-signature requests; status callbacks file into matter | `/api/connectors/docusign/webhook` | `account_id`, `integration_key`, `oauth_token_secret_ref` |
| `quickbooks` | outbound + callback | Two-way sync of invoices, payments, expenses | `/api/connectors/quickbooks/webhook` | `realm_id`, `oauth_token_secret_ref` |
| `outlook` | bidirectional (Graph API) | Email-to-matter filing; calendar sync | `/api/connectors/outlook/webhook` | `m365_tenant_id`, `oauth_token_secret_ref`, `user_mapping_json` |
| `dropbox` | outbound | Document storage replication | `/api/connectors/dropbox/webhook` | `app_key`, `oauth_token_secret_ref`, `folder_root` |

All secret references resolve at runtime from Azure Key Vault via managed identity. `connectors.config.json` at the project root carries refs only — never values.

## Background work

| Job | Trigger | Cadence | Outbox required? |
|---|---|---|---|
| Send invoice email | reactive (`invoice.sent`) | on-event | yes |
| Calendar reminder dispatch | scheduled | every 5 minutes | yes |
| Trust reconciliation reminder | scheduled | monthly cron (day 5 of month) | yes |
| Document indexing for AI | reactive (`document.uploaded`) | on-event | yes |
| Connector outbound dispatch | reactive | on-event | yes (the outbox IS the dispatch) |
| AI conversation cleanup | scheduled | nightly | no (housekeeping) |
| GDPR export job | reactive (`data_subject_request.received`) | on-event | yes |

Scheduler: a single in-process scheduler (locked in by `design-architecture`). Outbox: per-tenant single queue per `SPEC.md` answer #2.

## Open questions for design-architecture

1. **Outbox transport** — Postgres-backed (single dependency, simplest) or external broker (NATS, RabbitMQ, Service Bus)? For v1 size, Postgres outbox is sufficient.
2. **AI assistant memory store** — Postgres + pgvector (single DB) or dedicated vector store (Azure AI Search)? pgvector for v1; abstract behind `IVectorStore`.
3. **Document storage** — Azure Blob with private endpoint and per-matter SAS tokens, or single SAS per tenant? Per-matter is more secure; flag for cost analysis.
4. **Identity provider** — single B2C tenant for both staff and clients, or Entra ID for staff + a separate IdP for clients? Single B2C keeps things simpler.
5. **Multi-region** — single region (us-east) for v1 with documented "future regional" hooks, or two regions from day one? v1: single region.
6. **Chatbot UI placement** — slide-over panel on every page (per `industry-chatbot` pattern), or also a dedicated `/chat` route? Slide-over primary; dedicated route for power users.
7. **Scheduler choice** — Hangfire or Quartz.NET? Hangfire has a dashboard out of the box, which is a Firm Admin win.

## Answers (committed defaults — drive Phase 5)

1. **Postgres outbox.** Single dependency, scales fine for SMB firms. Documented switch path to Service Bus when load justifies.
2. **pgvector** behind `IVectorStore`. Single DB, abstraction-ready.
3. **Per-matter SAS tokens** (5-minute expiry, generated per access). Defensible audit story; cost is negligible at SMB volume.
4. **Single B2C tenant** for both staff and clients, with custom claim `tenant_id` and `role`.
5. **Single region** (us-east-2 / eastus2). Multi-region documented as future work.
6. **Slide-over chatbot panel** on every page (primary). No dedicated route — slide-over is always reachable.
7. **Hangfire** with Postgres storage provider. Dashboard at `/hangfire` (admin-only). Reasoning lives in `DECISIONS.md` ADR.
