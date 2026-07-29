# Legal-tech research refresh (2026) + Casewell feature integration plan

Refreshes `research/legal.md` (the original v1 research) against the 2026 competitive
landscape, then turns the gap into a prioritized plan. The single biggest finding isn't
external at all — it's an internal parity gap between Casewell's shipped module and a richer
legal module already built and tested inside the Cortex platform repo as a sample/fixture.
That's Priority 0: lower lift than any external integration, and already proven in CI.

## Priority 0 — port what's already built (no new design needed)

`samples/Cortex.Modules.Legal` in the **Cortex** repo (used there as the platform's demo/test
fixture for module composition, RAG, jobs, etc.) has, tested and working, several capabilities
Casewell's real product module (`src/Casewell.Legal`) never received:

| Capability | Sample module | Casewell today |
|---|---|---|
| Time tracking (`log_time`, `list_time`) — not approval-gated, append-only correction model | ✅ | ❌ |
| Pre-bill generation (time entries → PDF, filed on matter) | ✅ | ❌ — **README claims this ships** ("Time & pre-bills"); it does not |
| Matter tasks (`add_task`/`list_tasks`/`complete_task`) | ✅ | ❌ |
| Deadlines as a first-class model: reminder lead time, completion tracking, `list_deadlines` distinct from generic events | ✅ | ⚠️ Casewell has generic calendar events only (deadline/hearing/meeting/reminder as one type, no reminder lead time, no completion state) |
| Matter close with a completeness gate (refuses while deadlines/tasks are open unless forced) + reopen | ✅ | ⚠️ Casewell's `set_matter_status` allows closing unconditionally |
| Client-facing status-update letter drafting (strips internal notes/strategy) | ✅ | ❌ |
| One-look matter brief ("brief me on Meridian") — status, parties, open deadlines, tasks, time totals, recent documents | ✅ | ❌ — the README's own example query has no tool behind it |

Casewell is ahead in two places the sample module never got: tamper-evident hash-chained
conflict attestations (ADR-0006) and document-template assembly (`draft_from_template`).
Porting is not a one-way copy — reconcile the deadline/event model and the conflict-check
verb choice (`adverse` vs `opposing`) once, in Casewell's favor where Casewell is stricter.

**Why this is Priority 0**: every one of these tools is already implemented, described, and
covered by tests in the Cortex repo — this is a port + reconciliation, not new design. It also
directly fixes a truth-in-advertising gap: the README's own "A Tuesday, with Casewell" example
queries ("Log half an hour", "Brief me on Meridian") describe tools that don't exist yet.

## What changed in the market since the v1 research

- **Legal AI has bifurcated into two tiers.** Enterprise-native platforms (Harvey — $11B
  valuation, 100k+ lawyers, Vault + Workflows + 25,000+ custom agents; Legora — contract
  review at portfolio scale for M&A/diligence) sell to Am Law firms and in-house teams.
  Word-native tools (Spellbook — Review/Draft/Ask/Benchmarks/Associate/Clause Library;
  Ironclad's Jurist AI agents) sell drafting/redlining into the existing MS Word workflow.
  Casewell's chat-first, matter-centric model is a third lane neither occupies well.
- **Trust accounting is now a genuine differentiator, not table stakes.** CosmoLex ships full
  IOLTA/GL/AP at every tier; Clio gates it behind a paid add-on. Casewell has none yet — see
  Priority 2.
- **Docketing has a rules-based tier above what any of us do.** CompuLaw/Aderant and
  LawToolBox calculate deadlines from actual court rules across 2,500+ jurisdictions,
  maintained by licensed-attorney staff watching for rule changes. That's a durable moat none
  of us should try to build from scratch — an integration/connector target, not a build target.
- **No open-source competitor is AI-native.** ArkCase, ClinicCases, and Worklenz are the
  self-hosted options and none run agent-mediated, permission-checked, audited actions —
  Casewell's actual differentiator (free, open-source, MIT, AI-native, accountable-by-design)
  remains uncontested. The market refresh didn't surface anyone closing that gap.
- **E-discovery and litigation analytics stayed enterprise-only** (Everlaw predictive coding,
  DISCO's agentic Cecilia AI) — confirmed out-of-scope for Casewell's target segment (solo/small
  firm), same call as the v1 research.

## Priority 1 — closes the "Tuesday" story, cheap relative to value

1. **Deadline reminders that actually notify** — Casewell's calendar events don't fire
   anything; `list_upcoming_events` is pull-only. Wire `add_matter_event` (or its ported
   `add_deadline` replacement) to `INotificationChannel` (already shipped in Cortex — email +
   WhatsApp channels both exist) so the two-stage reminder the README already promises is real,
   not aspirational.
2. **Client portal / status visibility** — every incumbent (Clio Connect, MyCase, Filevine)
   treats a client portal as baseline. Casewell's client-facing status letter (Priority 0) is
   the content; the delivery gap is a portal or, cheaper first cut, scheduled email delivery
   of that letter through the already-shipped email channel.
3. **E-signature on generated documents** — `generate_pdf` produces the NDA/letter; nothing
   gets it signed. A DocuSign/dropbox-sign connector (thin, OAuth, follows the msgraph/
   Google-Drive connector pattern already in Cortex) turns "drafts an NDA" into "drafts,
   sends for signature, and files the executed copy" — the single most-cited feature across
   every incumbent reviewed (Clio, MyCase, Smokeball all lead with it).

## Priority 2 — real differentiation, larger lift

4. **Trust accounting / IOLTA** — the CosmoLex gap. A dedicated ledger entity (trust
   transactions per matter, three-way reconciliation) is a genuine new capability, not a port;
   worth a dedicated design pass given the compliance stakes (this is the one area where a
   bug is a bar-complaint risk, not a UX complaint).
5. **Court-rules deadline calculation connector** — don't build a rules engine; integrate one.
   LawToolBox and CompuLaw both expose the "given jurisdiction + trigger event, calculate every
   downstream deadline" capability as a service. A connector that calls out and dockets the
   result fits Casewell's existing connector SDK exactly (same shape as the email-intake
   channel: external system → structured Casewell records).
6. **Contract redlining in Word** — Spellbook's actual product wedge. Lower priority for
   Casewell specifically: it competes with the clause-library/playbook chain Casewell already
   has, rather than filling a gap, and would mean shipping a Word add-in (new surface,
   new maintenance burden) rather than deepening the chat-first model.

## Explicitly out of scope

- **E-discovery / litigation analytics** (Everlaw/DISCO tier) — wrong segment; confirmed again
  this refresh.
- **Enterprise CLM** (Ironclad-scale, cross-department contract lifecycle at F500 volume) —
  wrong buyer.
- **Legal research** (Westlaw/Lexis/Descrybe.ai-style case-law search) — a RAG-over-case-law
  connector is thinkable later, but it's a distinct product decision (accuracy/liability
  profile is different from drafting-assistance), not a "few more tools" addition.

## Sources consulted this refresh

- [Case Management Software Comparison 2026](https://mylegalacademy.com/kb/case-management-software-comparison-2026), [Clio vs MyCase vs Smokeball pricing](https://purple.law/blog/clio-vs-mycase-vs-smokeball/), [Clio vs MyCase vs Filevine](https://www.truereview.co/post/clio-vs-mycase-vs-filevine)
- [Legora vs Harvey](https://spellbook.com/briefs/legora-vs-harvey), [Best Legal AI Tools 2026](https://gc.ai/blog/legal-ai-tools), [AI-Powered Legal Technology Companies guide](https://www.forbes.com/sites/allbusiness/2026/05/16/a-guide-to-ai-powered-legal-technology-companies/)
- [Legal client intake / CRM guide](https://www.legalintaker.com/blog/legal-practice-management-software-2026), [Lawmatics](https://www.lawmatics.com/client-intake)
- [Gavel document automation](https://www.gavel.io/), [HotDocs vs Gavel comparison](https://www.gavel.io/resources/best-document-automation-software)
- [CompuLaw / Aderant](https://www.aderant.com/solutions-compulaw/), [CourtDrive](https://www.courtdrive.com/), [LawToolBox deadline calculator](https://lawtoolbox.com/deadline-calculator/)
- [LawDroid](https://lawdroid.ai/), [Descrybe.ai](https://growlaw.co/blog/ai-for-legal-research), [PocketLaw](https://valuecore.ai/valuehub/organizations/pocketlaw)
- [Everlaw](https://www.everlaw.com/), [DISCO Cecilia AI](https://csdisco.com/blog/how-artificial-intelligence-transforms-ediscovery), [AI contract redlining](https://www.buildmvpfast.com/blog/ai-contract-review-harvey-spellbook-law-firm-roi-2026)
- [Legal spend management / CounselLink+](https://www.lexisnexis.com/en-us/products/counsellink/legal-spend-management.page), [Legal billing software 2026](https://www.consultwebs.com/blog/legal-billing-software-the-15-best-options-in-2026/)
- [Open-source legal case management (ArkCase, ClinicCases, Worklenz)](https://www.goodfirms.co/legal-case-management-software/blog/best-free-open-source-legal-case-management-software-solutions)

## Answers (researched decisions, 2026-07-09)

The user delegated the three open domain questions; each answer below is grounded in the
cited sources and is now implemented or designed accordingly.

**1. Status-letter delivery: review-first, explicit send — never auto-send.**
[ABA Formal Opinion 512](https://www.americanbar.org/content/dam/aba/administrative/professional_responsibility/ethics-opinions/aba-formal-opinion-512.pdf)
(July 2024) requires lawyers to review generative-AI output before relying on it, and firm
practice guidance is that AI-assisted work product is reviewed by a supervising lawyer before
it leaves the firm. Communication-automation guidance ([Clio](https://www.clio.com/blog/law-firm-client-communication/),
[Legal Authority](https://legalauthority.io/automated-client-updates/)) draws the same line:
automated scheduling is fine, unreviewed substance is not — and bad news never comes from a
robot. **Implemented**: `draft_status_update` files the draft; a separate, approval-gated,
outward-facing `send_status_update` emails it to the matter's client email (a new
`Matter.ClientEmail`) and files the sent copy. No client email or no SMTP → it refuses and
the letter stays a draft.

**2. E-signature: Documenso first, DocuSign later if demanded.**
[Documenso](https://documenso.com/) is the open-source e-signature platform (AGPL-3.0,
self-hostable, REST API + webhooks) — the ethos match for a free, open-source product, and
its API-token auth makes it a *service-mode* connector (simpler than DocuSign's OAuth; we
call the HTTP API only, so its license does not touch our MIT code). DocuSign remains the
enterprise standard ([comparison](https://www.esign.ai/blog/docusign-vs-docusign-api-rate-limits-pricing-tier-review-2026))
and can ship later as a second adapter behind the same seam. **Implemented** (Cortex):
`documenso` connector — send a stored document for signature (approval-gated), check status,
fetch the signed copy back into the file store.

**3. Trust accounting v1: a matter-scoped ledger with fail-closed guards — no bank
integration required.** The rules are concrete
([Model Rule 1.15](https://www.americanbar.org/groups/law_practice/resources/law-technology-today/2024/a-guide-to-ensuring-iolta-account-compliance/),
[three-way reconciliation guides](https://caretlegal.com/blog/three-way-trust-reconciliation-explained/)):
client funds live in a separate trust account, every matter keeps its own ledger, **a negative
client-ledger balance is treated as misappropriation even when the account total is positive**,
and most bars mandate a monthly three-way reconciliation (bank statement = book balance = sum
of client ledgers), retained 5–7 years. **Designed** (next build): `TrustTransaction`
(matter-scoped deposit/disbursement, approval-gated, append-only), the fail-closed guard —
a disbursement exceeding the matter's trust balance is refused, not warned —
`trust_balance`/`list_trust_transactions`, and `export_trust_reconciliation`: the three-way
worksheet (user supplies the bank-statement figure; the module supplies the other two legs)
rendered to PDF and filed. Bank feeds are explicitly out of scope for v1: the ledger records
what happened at the bank; it does not move money.
