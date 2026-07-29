# Security policy

## Reporting a vulnerability

Please report security issues privately. Do **not** open a public GitHub issue.

- **Email**: abraham.fdzg@gmail.com
- Use subject prefix `[security] Casewell:`
- Expect an acknowledgement within 7 days

We follow a 90-day coordinated disclosure model. Once a fix is shipped we credit the reporter (unless they request anonymity) in the release notes.

## Scope

In scope:

- The .NET solution under `src/`
- The SPA under `web/casewell-web/`
- Terraform under `infra/azure/`
- CI workflows under `.github/workflows/`
- Configuration (`appsettings.json`, `workflow.json`, `.claude/settings.json`)
- Generated documents containing security-relevant decisions (`SPEC.md`, `PLAN.md`, `ARCH.md`, `DECISIONS.md`)

Out of scope:

- Vulnerabilities in third-party packages (`Microsoft.*`, `Hangfire.*`, `Npgsql.*`, npm dependencies). Report those upstream; we update affected versions promptly.
- Vulnerabilities in Azure services themselves.
- Issues in declared external Claude Code plugin marketplaces (`dotnet/skills`, `anthropics/skills`). Report those to the marketplace owners.

## Threat model

Casewell holds attorney-client privileged information. Privilege attaches per-matter; isolation is mission-critical.

### High-impact threats

1. **Cross-tenant data leak** — a query returning one firm's data to another firm. Mitigated by EF Core global query filter on every `ITenantedEntity`; bypass requires explicit `IgnoreTenantFilter()` and is audit-logged.
2. **Cross-matter data leak inside a tenant** — an attorney without access to a matter seeing its content. Mitigated by per-matter authorization on every endpoint, document SAS tokens scoped per matter, and the `Matter.Self.View` policy for clients.
3. **Trust-account commingling or overdraft** — disbarment-grade clerical error. Mitigated by API-layer hard guardrails returning Problem Details on attempted invalid posts. Supervisor override requires the `Trust.Override` policy plus audit-logged reason.
4. **Privileged information exfiltration via AI** — matter content sent to a foundation-model training pipeline. Mitigated by private-endpoint Claude API contract with no-retention; the system prompt explicitly forbids retention or repetition outside the conversation.
5. **Token / secret leakage** — connector tokens, DB credentials, Claude API key exposed in logs or configs. Mitigated by Key Vault for every secret, managed identity for resolution, audit log redacts `[Pii]`-marked fields, and the `theworkflow` plugin's `secret-detection` protocol scans `workflow.json` before write.
6. **Phishing / fake e-signature / impersonation** — a malicious actor sends an "engagement letter" pretending to be the firm. Mitigated by DocuSign-issued signing URLs that bind to the matter + audit-logged signing events.
7. **Audit log tampering** — an actor modifies the audit log to hide their tracks. Mitigated by separate `audit` schema with append-only semantics (no `UPDATE`/`DELETE` granted to the application role) and per-row hash chaining for the most sensitive log types (`ConflictAttestation` already implements this).

### Medium-impact threats

- **DoS via expensive AI queries** — rate-limited per tenant (default 100 req/min), AI calls additionally throttled per user via Redis-backed sliding window.
- **Denial of service via document upload bombs** — multipart upload limited to 100 MB per file, with virus-scanning hook *(deferred to a later epic)*.
- **CSRF / XSS on the SPA** — CSP headers in production, SameSite cookies, no `dangerouslySetInnerHTML` without sanitisation review.

## Defence in depth

- **TLS everywhere**: HTTPS-only ingress, private endpoints inside VNet, TLS 1.2+ on every dependency.
- **Encryption at rest**: Postgres (Azure default), Blob (Azure default), Key Vault (HSM-backed in prod).
- **Identity**: Azure AD B2C with custom user flows for staff vs clients. MFA enforced on staff sign-in flow.
- **Authorization**: policy-based (`<Module>.<Action>`), role-to-policy binding in config, no role checks in code.
- **Auditing**: every write captured in the `audit` schema with before/after snapshots. PII redacted via `[Pii]` attribute.
- **Outbox**: every external side effect goes through the outbox; failures retry with backoff and never silently drop.
- **Idempotency**: every write endpoint accepts `Idempotency-Key`; 24h replay window in Redis prevents duplicate side effects on retry.

## ABA / state-bar compliance posture

See [`research/legal.md`](research/legal.md) → *Compliance / regulatory considerations* and [`SPEC.md`](SPEC.md) → *Regulatory constraints* for the regulation list and how each is honoured.

- **ABA Rule 1.6 (confidentiality)** — encryption, per-matter access control, SOC 2 Type II posture (targeted for first audit at v2).
- **ABA Rule 1.15 (safekeeping)** — trust accounting hard guardrails enforce no-commingling, no-overdraft, three-way reconciliation.
- **ABA Rule 5.3 (nonlawyer assistance)** — AI-drafted client-facing output requires explicit attorney approval before send.
- **Attorney-client privilege** — Claude API contract with no-retention; matter content never enters training data.

## Vendor diligence summary

- **Anthropic** — DPA in place, no-retention contract, private endpoint hosting.
- **DocuSign** — SOC 2 Type II, BAA available if needed for PI/HIPAA-adjacent matters.
- **QuickBooks Online (Intuit)** — SOC 2 Type II, signed DPA.
- **Slack** — SOC 2 Type II, signed DPA, no PHI permitted.
- **Microsoft (Azure + B2C + Graph)** — comprehensive certifications (FedRAMP, HIPAA-ready, SOC 2, ISO 27001).
- **Dropbox** — SOC 2 Type II, BAA available.

A vendor due-diligence file is maintained per vendor in `infra/azure/vendor-diligence/` (added with the Connectors epic).
