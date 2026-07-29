# Operations

Deployment, environments, runbooks, and the compliance procedures every Casewell operator needs to know.

## Environments

| Env | Purpose | Region | Postgres tier | Redis tier | HA / Geo-redundancy |
|---|---|---|---|---|---|
| `dev` | Local + shared dev cloud | eastus2 | B_Standard_B1ms | Basic C0 | none |
| `staging` | Pre-prod validation | eastus2 | GP_Standard_D2ds_v5 | Standard C0 | optional |
| `prod` | Production | eastus2 | GP_Standard_D2ds_v5 zone-redundant | Standard C0 | GRS backups + HA Postgres |

Per [ARCH.md ADR-0005](DECISIONS.md), v1 is single-region. Multi-region is documented as future work.

## Local development

```bash
# 1. One-time setup
dotnet workload restore
cd web/casewell-web && npm install && cd -

# 2. Start the whole stack
dotnet run --project src/Casewell.AppHost

# 3. The Aspire dashboard prints URLs for: api, web, postgres, redis, pgadmin.
```

### Database migrations

Migrations live with the Infrastructure project. EF Core CLI is required (`dotnet tool install --global dotnet-ef`).

```bash
# Add a new migration
dotnet ef migrations add <DescriptiveName> \
  --project src/Casewell.Infrastructure \
  --startup-project src/Casewell.Api

# Apply migrations locally (Aspire AppHost wires the connection string)
dotnet ef database update \
  --project src/Casewell.Infrastructure \
  --startup-project src/Casewell.Api
```

Production: migrations run as a separate Container App job (not inline at app startup). The deploy pipeline runs `dotnet ef migrations script` in CI, then applies the script in a dedicated job that completes before the API rolls out.

## Cloud deploy

Prerequisites: Azure CLI, Terraform 1.9+, Azure AD permissions to create resource groups, an admin password resolvable from Key Vault (`TF_VAR_postgres_admin_password`).

```bash
cd infra/azure

terraform init \
  -backend-config="resource_group_name=tfstate-rg" \
  -backend-config="storage_account_name=tfstate<unique>" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=casewell-${ENVIRONMENT}.tfstate"

terraform plan -var-file=tfvars/${ENVIRONMENT}.tfvars
terraform apply -var-file=tfvars/${ENVIRONMENT}.tfvars
```

Container Apps for the Api, Web, and Hangfire dashboard are provisioned by the Aspire-generated deployment manifest via `azd up` from the AppHost project — separate from the Terraform run that owns shared infra.

## Secrets

| Secret | Source | Consumed by |
|---|---|---|
| Postgres admin password | Key Vault (`postgres-admin-password`) | Terraform (`TF_VAR_postgres_admin_password`) at apply time |
| App DB connection string | Composed from managed identity + Postgres FQDN | API container at runtime |
| Connector tokens (Slack, DocuSign, QBO, Outlook, Dropbox) | Key Vault, one secret per `<connector>-<tenant>-<key>` | API container at runtime |
| Claude API key | Key Vault (`claude-api-key`) | MAF assistant via `IAiAssistantService` |
| OpenTelemetry OTLP endpoint | App config (Container Apps env vars) | ServiceDefaults at startup |

No secret ever lives in `appsettings.json` or in `connectors.config.json`. Refs only.

## Runbooks

### Trust-account discrepancy alert

A reconciliation that reports `BankBalance ≠ BookBalance` triggers an alert on the Firm Admin's home dashboard.

1. Open the affected trust account in the Trust epic UI.
2. Inspect the latest `Reconciliation` row and the lines that don't tie.
3. If the discrepancy is a bank-side error, attach the bank statement and mark the reconciliation `pending-bank-correction`.
4. If it's a book-side error, file an `Adjustment` ledger entry with full notes and a supervisor sign-off.
5. Re-run the reconciliation. The audit log captures every entry plus the supervisor approval chain.

### A connector starts failing

1. Open `/hangfire` (firm-admin only). Look for the connector's outbox-dispatcher job.
2. Inspect the most recent failed exception.
3. If transient (network, 5xx), Hangfire auto-retries with backoff — no action needed.
4. If persistent (auth, 4xx), refresh the connector's tokens in Key Vault and toggle the connector `Disabled → Enabled` in `/integrations`.
5. If the issue is on the upstream side (Slack, DocuSign down), pause the connector in `/integrations` to stop queueing further work.

### AI assistant returning hallucinated facts

1. Open the matter in question.
2. Inspect the AI conversation log — every turn shows the documents that were grounded into the response.
3. If grounding is missing (the AI answered from prior context only, not from matter documents), the embedding index for that matter is likely stale — trigger a re-index from the Settings → AI page.
4. If grounding is present but wrong, file an issue with the matter ID and the turn ID. We use these to tune the system prompt and tool descriptions.

## GDPR / compliance procedures

### Data export (Article 20)

A data subject (typically a client) requests export of their personal data:

1. Firm Admin (or designated DPO) navigates to `/settings/compliance/data-subject-request`.
2. Identifies the data subject by either user account or contact record.
3. Submits the request. The API enqueues a Hangfire job that:
   - Collects every row referencing the subject's `ContactId`.
   - Redacts non-subject PII (e.g. opposing parties on the same matter).
   - Bundles as a downloadable archive (JSON + linked blobs).
   - Notifies the requestor when the archive is ready.

### Data deletion (Article 17)

Erasure conflicts with litigation hold. The policy:

1. Firm Admin opens the data-subject record in `/settings/compliance`.
2. Marks subject as `right-to-erasure-requested`.
3. The job:
   - Identifies every matter where the subject is a party.
   - For matters with `LegalHoldFlag = true`, the deletion is *deferred* — the matter is flagged and a quarterly review reminder is scheduled.
   - For matters without legal hold, performs the deletion (hard delete on operational; audit log records who/when, retains only the deletion record + the prior-state hash).

Both flows are audit-logged with full attribution.

### Trust-account compliance (ABA Rule 1.15)

Every month:

1. The Trust epic's reconciliation reminder fires on day 5 of the month (per `PLAN.md` Background work).
2. Firm Admin runs the three-way reconciliation: bank statement balance ↔ book balance ↔ matter sub-ledgers.
3. The reconciliation is signed off by a second user (separation of duties).
4. The signed reconciliation PDF is filed under the firm's compliance documents store.

API-layer hard guardrails prevent commingling at write time: a ledger post that would cross funds between matters or overdraw is rejected with Problem Details. Override requires a supervisor with explicit `Trust.Override` policy + audit-logged reason.

## Observability

- Traces, metrics, logs flow to Azure Monitor via OTLP.
- `tenant_id` is included as OTel baggage on every trace — filter dashboards by tenant in App Insights.
- Alerts (configured separately):
  - p95 API latency > 500ms (warning) / 1s (critical) for 5 minutes
  - Outbox dispatch failures > 10 in 15 minutes
  - Hangfire job failure rate > 5% in 1 hour
  - Trust ledger guardrail rejection > 0 (informational — investigate intent)

## Backup / restore

- Postgres: Azure-managed automated backups, 7-day retention in dev, 35-day in prod, geo-redundant in prod.
- Blob: GRS replication in prod. Soft delete enabled, 30-day retention.
- Audit schema is backed up alongside operational data but is restorable independently if needed (separate schema, separate restore script).

## Disaster recovery

- RPO target: 15 minutes (Postgres point-in-time-restore).
- RTO target: 4 hours (re-deploy stack via Terraform + Aspire manifest + restore latest DB backup).
- Drill cadence: quarterly. Drill documents land in `infra/azure/drill-reports/`.
