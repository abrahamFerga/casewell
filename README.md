<div align="center">

# ⚖️ Casewell

**The free, open-source AI assistant that runs a law practice — and leaves you in charge.**

Matters · Docketing · Conflict checks · Drafting · Time & pre-bills · Client intake over WhatsApp

*Free to use. MIT licensed. Every AI action permission-checked, approval-gated, and audited.*

</div>

---

## What it does

Casewell is a domain application for lawyers and small firms. It's organized the way a
practice is — around **matters** — and its AI assistant does real work inside them:

| You say | It does |
|---|---|
| *"Run a conflict check on Meridian Holdings"* | Checks every recorded party across your matters; the attestation is hash-chained, tamper-evident |
| *"The answer is due August 14"* | Dockets the deadline with two-stage reminders — nothing lives in anyone's memory |
| *"Draft an NDA and file it on the matter"* | Drafts from **your** clause library, renders the PDF, and waits for **your approval** before filing |
| *"Log half an hour — call with opposing counsel"* | A billable time entry, captured the moment you mention it |
| *"Review this contract against our playbook"* | Every rule checked, every finding cited to the file |
| *"Brief me on Meridian"* | Parties, deadlines, tasks, hours, documents — one look |

Clients can reach the practice over **WhatsApp** or the firm's **intake email address** — messages and attachments land as conflict-checkable intake, filed where they belong. The firm's existing documents stay where they are: connect **SharePoint/OneDrive, Google Drive, an S3 bucket, or Azure Blob** and the assistant works on them in place.

## Why it's different

Most AI legal tools are a chat window. This one is an **accountable system**:

- **Permission-checked before the AI** — the model never sees an action the signed-in user
  isn't allowed to take. Roles (paralegal, associate, partner…) are yours to shape at runtime.
- **You keep signature authority** — anything that changes the record waits for a human
  approval, in the conversation. Time capture is the one deliberate exception.
- **Everything audited** — tool calls, sign-ins, permission changes, token spend: append-only.
- **Ethical walls that hold** — a restricted matter vanishes from every tool, tab, and search
  for everyone outside the wall, whatever their other permissions.
- **Never legal advice** — drafts are starting templates for attorney review; the assistant
  never invents statutes or citations and flags new language as new.

## Free to use

Casewell is **MIT-licensed and free to run yourself** — your hardware or your cloud, your
data, your AI keys. Run it with the built-in keyless Mock provider to try everything, then
plug in your own OpenAI, Azure OpenAI, Anthropic, or Ollama key when you're ready (keys are
stored write-only, encrypted).

A hosted version with automatic provisioning is being built on the same code — self-hosting
stays free either way.

## Quick start

Prerequisites: .NET 10 SDK, Docker Desktop, Node 20+, and a sibling checkout of
[Cortex](https://github.com/abrahamFerga/Cortex) (the platform the product is built on;
its UI packages run from source until they publish to npm).

```bash
git clone https://github.com/abrahamFerga/casewell
git clone https://github.com/abrahamFerga/Cortex     # sibling directory
cd casewell
dotnet run --project src/Casewell.AppHost
```

The Aspire dashboard opens with everything running: the API, Postgres (pgvector), Redis, the
workspace UI, and the admin console. Sign in (dev auth needs no identity provider), open your
first matter, and docket something. **No AI key required** — the Mock provider exercises the
entire pipeline, approvals and audit included.

## Built on Cortex

Casewell is a product on the [Cortex platform](https://github.com/abrahamFerga/Cortex):
the legal domain lives in this repo; the security spine — multi-tenant isolation, RBAC,
human-in-the-loop approvals, audit, budgets, channels — comes from the platform packages and
is shared by every Cortex product. That's why a small product ships enterprise controls.

## Documentation

- **Design history** — [`SPEC.md`](SPEC.md), [`PLAN.md`](PLAN.md), [`ARCH.md`](ARCH.md),
  [`DECISIONS.md`](DECISIONS.md), and [`research/legal.md`](research/legal.md) record how the
  product was researched and planned. Treat them as history: the shipped product is the truth.
- **Operating & security** — [`OPERATIONS.md`](OPERATIONS.md), [`SECURITY.md`](SECURITY.md).

## License

[MIT](LICENSE). AI output is a starting point for professional review — never legal advice.
