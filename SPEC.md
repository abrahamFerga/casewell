# Casewell — Product specification

## In one sentence

Casewell is a multi-tenant practice management platform for US-based small law firms (5–25 attorneys) that takes a matter from intake through invoice and trust-account reconciliation, with an AI assistant that answers questions about any matter on demand.

## Primary jobs to be done

- **When** a prospect calls about a new matter, **I want** to capture their info and run a conflict check without leaving my desk, **so that** I can decide whether to take the matter before the call ends.
- **When** I'm working on a matter, **I want** a lightweight in-context timer with auto-suggest of the activity code, **so that** I don't lose 20%+ of my billable hours to forgotten entries.
- **When** my client asks "where are we on this," **I want** to find the latest document, the last communication, and the next deadline in under 30 seconds, **so that** I never look unprepared.
- **When** I close a billing cycle, **I want** to generate a defensible invoice from time + expenses + flat fees in one click, **so that** billing day takes hours instead of days.
- **When** money comes into the trust account, **I want** it segregated, reconciled, and protected from accidental commingling, **so that** I don't get disbarred over a clerical error.
- **When** I need to brief myself on a matter I haven't touched in weeks, **I want** the AI assistant to draft a one-page summary from the matter file, **so that** I review and edit instead of starting from scratch.

## Target personas

- **Attorney (Senior/Partner)** — owns matters, bills time, reviews and approves invoices, supervises junior staff. Top 3 tasks:
  1. Open a new matter and complete intake (including conflict-check attestation).
  2. Capture time, draft invoices, approve invoices for send.
  3. Pull matter status (docs, calendar, next deadline, last communication) for client check-ins.
- **Paralegal** — drafts documents, manages files, owns the matter calendar. Top 3 tasks:
  1. Draft documents from templates with matter data merged in.
  2. Maintain the matter calendar (deadlines, hearings, court dates).
  3. Organize matter documents (folders, versions, secure sharing with clients).
- **Firm Admin / Bookkeeper** — manages billing, payments, trust accounting, firm-wide reporting. Top 3 tasks:
  1. Run monthly three-way trust account reconciliation.
  2. Apply received payments to invoices; manage A/R aging.
  3. Generate firm-wide reports (origination, realization, WIP, productivity by timekeeper).
- **Client (read-only via portal)** — views matter status, pays invoices, signs documents. Top 3 tasks:
  1. View matter status, upcoming deadlines, recent activity.
  2. Pay invoices (credit card / ACH) with IOLTA-compliant splits.
  3. Sign engagement letters and documents via embedded DocuSign.

## Capabilities

### Must have (v1)

| Capability | One-line description | Personas |
|---|---|---|
| Matter management | Central record per matter: parties, custom fields, status, assignments, audit trail | Attorney, Paralegal, Firm Admin |
| Trust accounting (IOLTA) | Segregated trust ledger with three-way reconciliation; per-jurisdiction config; API-layer guardrails against commingling/overdraft | Firm Admin, Attorney |
| Billing & invoicing | Time + expense + flat-fee invoice generation; multiple fee arrangements; approval workflow | Attorney, Firm Admin |
| Document management + automation | Per-matter file store with versioning + Word-template merge | Paralegal, Attorney |
| Calendar with reminders | Matter-linked events + Outlook sync; manual deadline entry with reminder rules | Attorney, Paralegal |
| Client portal | Secure web portal for matter status, invoice payment, document signing | Client, Attorney |
| RBAC + multi-tenancy | 5 roles (attorney, paralegal, firm-admin, bookkeeper, client) with policy-based authorization; tenant isolation at the data layer | All |

### Differentiators (v1)

| Capability | Why it matters | Personas |
|---|---|---|
| Matter-scoped AI assistant | Claude-powered chatbot scoped per matter; answers "where are we," drafts summaries + correspondence; runs against private endpoints with no-retention contract. Clio Manage AI and Smokeball Archie are the only competitive offerings. | Attorney, Paralegal |
| Tamper-evident conflict-check workflow | Conflict-check is more than a checkbox — every attestation is hashed + audit-logged + exportable, satisfying ABA 1.7/1.9/1.10 with evidence. Competitors do an attestation but don't produce an audit-grade record. | Attorney, Firm Admin |
| Pluggable channel connectors | Firms connect Slack / Teams / email / DocuSign per-tenant; messages and signatures auto-file to the matter. Demonstrates that we are an open platform, not a walled garden. | All |

### Explicitly out of scope (v1)

- **Court e-filing** — jurisdiction-by-jurisdiction certification work; integration burden far exceeds v1 demo value.
- **Native general ledger** — QuickBooks two-way integration covers it for 90% of firms.
- **Passive time capture (Smokeball AutoTime equivalent)** — requires desktop agent; major platform investment, no portfolio payoff.
- **50-state court-rules engine** — Clio's moat; build/buy decision deferred. v1 ships manual deadline entry with reminders.
- **Two-way SMS** — TCPA + 10DLC compliance overhead disproportionate to demo value.
- **Native mobile apps** — v1 ships PWA only. iOS/Android wrappers deferred.
- **Practice-area form libraries** — content acquisition problem, not a software problem.
- **Marketing/SEO/intake automation beyond a basic form** — Clio Grow territory, orthogonal to PM demo.
- **EU/UK certification** — GDPR/CCPA hooks (PII tagging, audit log, export/delete endpoints) are wired for "future ready" posture; not certified or marketed for v1.

## RBAC model (initial)

- **`attorney`** — Full read/write on assigned matters; can open new matters; can bill time; can draft invoices on assigned matters; cannot approve own invoices (separation of duties).
- **`paralegal`** — Read/write on assigned matters; can draft documents and invoices; cannot approve invoices; cannot post to trust account.
- **`firm-admin`** — All matters + RBAC config + firm settings + accounting. Cannot bill time as themselves on a matter they admin (separation of duties).
- **`bookkeeper`** — Accounting-only views: trust ledger, A/R aging, invoice posting, reconciliation. Read on matter financials only; no access to substantive matter content (privilege boundary).
- **`client`** — Own matters only, via portal. Read matter status + invoices; pay invoices; sign documents. No write access to matter content.

Roles map to ASP.NET Core authorization policies; code references policy names (`Matters.View`, `Matters.Open`, `Trust.Post`, `Invoices.Approve`), not role names.

## Regulatory constraints

- **ABA Model Rule 1.6 (Confidentiality)** — drives SOC 2 Type II posture, encryption-at-rest/in-transit, per-matter access controls, vendor due diligence on every connector.
- **ABA Model Rule 1.15 (Safekeeping Property)** — trust accounting must enforce no-commingling, no-overdraft, three-way reconciliation; v1 implements as API-layer hard guardrails returning Problem Details on violation.
- **State IOLTA program rules** (e.g., NY 22 NYCRR 1300.1, CA Rule 1.15, TX Rule 1.14) — per-jurisdiction trust-account config; supports multi-jurisdiction firms with separate trust accounts per state.
- **ABA Rules 1.7 / 1.9 / 1.10 (Conflicts of Interest)** — conflict-check at intake searches across contacts, matters, parties; produces a hash-chained audit record per attestation.
- **ABA Rule 5.3 (Nonlawyer Assistance)** — AI-drafted client-facing output requires explicit attorney approval before send; UI surfaces the AI-drafted-on banner.
- **Attorney-client privilege & work-product doctrine** — AI assistant uses private endpoints with no-retention contracts; matter content never enters foundation-model training data.
- **GDPR/CCPA hooks (not certified)** — PII attribute on data model; audit log includes who/what/when/before/after; `/api/v1/tenants/{id}/export` and `/api/v1/tenants/{id}/data-subject-request` endpoints implemented but unmarketed in v1.
- **PCI-DSS SAQ-A** — payment processing goes through tokenized provider (DocuSign for e-sig + QuickBooks for payments); we do not store PAN.
- **ABA technology competence (Rule 1.1 Comment [8])** — drives in-app changelogs visible to attorneys + plain-English audit log views.

## Success metrics

- **Time-to-first-matter ≤ 5 minutes** from new tenant signup to first matter opened (activation).
- **% of matters with at least 1 document, 1 time entry, and 1 calendar event after 7 days ≥ 70%** (engagement / "matter has a heartbeat").
- **Trust-account reconciliation completion rate ≥ 95% within 5 business days of month-end** (compliance signal; surfaced on Firm Admin home).
- **AI chatbot helpfulness ≥ 4.0/5** via instrumented thumbs-up/down on every response.
- **Billing-cycle duration ≤ 24 hours** from matter close (or month-end) to invoice sent.

All metrics observable from telemetry — no metric depends on a survey or qualitative input.

## Open questions for plan-system

1. **Bounded contexts** — group `Matters / Documents / Calendar` together (single context, simpler joins) or split `Documents` out for storage scale? Recommendation slot: same context for v1, extract if/when doc storage hits scale problems.
2. **Outbox topology** — single per-tenant outbox queue or per-aggregate queues? Affects ordering guarantees and reprocessing scope.
3. **Audit log destination** — same Postgres DB (separate schema) or separate Postgres instance? Separate is cleaner for "delete from operational ≠ delete from audit"; same is simpler for v1.
4. **Conflict-check data model** — separate `Conflict` aggregate with snapshot at attestation time, or query-time join across `Contacts + Matters + Parties`? Snapshot is more defensible legally; join is cheaper.
5. **Trust account model** — single trust account per firm (US single-state firms), or per-IOLTA-jurisdiction (multi-state firms)? Data model must support both.
6. **AI architecture** — call Claude directly from `Application` via MAF, or route through a `IAiAssistantService` abstraction so future providers (private Azure OpenAI, on-prem) can slot in? Recommendation slot: abstraction for v1, single Claude impl, swap-ready.
7. **PWA vs SPA vs SSR** — PWA gives offline + installable; we're already locked to Vite + React + shadcn. Confirm we go PWA-via-Vite-plugin rather than skipping the offline manifest for v1.

## Answers (committed defaults — drive Phase 4)

1. Same context for `Matters/Documents/Calendar` in v1. Extract docs only if storage proves a problem (it won't for the demo).
2. **Per-tenant single outbox queue.** Simpler. Per-aggregate is over-engineering for v1.
3. **Separate Postgres schema in the same DB** for audit. Separation of concern, single backup boundary.
4. **Snapshot at attestation time**, hash-chained. Defensible legal record beats marginal cost savings.
5. **Per-IOLTA-jurisdiction trust accounts** supported from day one (most small firms are single-state, but the data model accommodates multi-state without migration).
6. **`IAiAssistantService` abstraction**. Single Claude implementation in v1. Swap-ready for Azure OpenAI / on-prem in v2.
7. **PWA via `vite-plugin-pwa`**. Offline manifest + service worker. Single web codebase.
