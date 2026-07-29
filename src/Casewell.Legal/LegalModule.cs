using Cortex.Application.Authorization;
using Cortex.Modules.Legal.Persistence;
using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Modules.Legal;

/// <summary>
/// The Legal vertical — a matter-centric legal assistant modeled on the market's table stakes
/// (see research/legal-ai.md and docs/LEGAL_VERTICAL_PLAN.md): matters as engagement workspaces,
/// documents attached to matters via the platform file store, a clause library, and drafting.
/// The same SDK, RBAC, audit, HITL approvals, token-usage, and chat channels apply with no
/// platform changes.
/// </summary>
public sealed class LegalModule : IModule
{
    public const string Id = "legal";

    public const string ViewClauses = "legal.clauses.view";
    public const string ViewMatters = "legal.matters.view";

    /// <summary>Curate the firm's clause library and playbook (add/remove entries).</summary>
    public const string ManageLibrary = "legal.library.manage";

    /// <summary>
    /// Hand-edit the practice's working records (matters, clients, deadlines, tasks, time) from
    /// the tab editors and the setup wizard. A person acting on a form needs no approval gate —
    /// RBAC is the gate; granted to firm-admin, deliberately not to paralegal.
    /// </summary>
    public const string Manage = "legal.manage";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Legal",
        Version = "1.9.0",
        Description = "Matter-centric legal assistant. Organize case documents into matters, search a clause library, and draft clauses for review.",
        Icon = "scale",
        AgentInstructions =
            "You are Cortex's legal assistant, organized around MATTERS (engagement workspaces). " +
            "When the user references a case or client engagement, work within that matter: use list_matters / " +
            "create_matter to resolve it, attach_document_to_matter to file documents the user sends (the message " +
            "carries a '[Attached files]' block with file ids), and list_matter_documents to see a matter's files. " +
            "To answer questions about a matter's documents: when the matter is indexed, PREFER search_knowledge " +
            "(scope it with collection 'matter: <name>') — it returns quoted passages with citations; offer " +
            "index_matter_documents when it is not indexed yet. For a single specific file, call read_document with " +
            "its file id. Either way, CITE the file name and id for every claim you take from a document — never " +
            "state document contents without a citation. " +
            "Track deadlines with the calendar tools: add_matter_event for court deadlines, hearings, and " +
            "reminders the user mentions; list_upcoming_events is the firm agenda — check it when the user asks " +
            "what needs attention, and flag OVERDUE items proactively; when an obligation is satisfied, " +
            "complete_event so it stops reminding. CLOSE-OUT: closing a matter checks completeness - open " +
            "events/tasks block it; only force after the user explicitly confirms. " +
            "BRIEFING: answer 'brief me on X' with get_matter_overview - one look, everything open. When the " +
            "client should hear where things stand, draft_status_update files a client-safe letter (progress, " +
            "dates, hours - nothing internal) as a DRAFT for attorney review. NEVER send to the client " +
            "unprompted: only after the user has reviewed a draft and asks, send_status_update emails it " +
            "to the client on file and files the sent copy. " +
            "TIME CAPTURE: the moment the user mentions work done ('log half an hour', 'call with opposing " +
            "counsel took 20 minutes'), record it with log_time (matter, hours, narrative description) — no " +
            "approval needed, it is the one quick-capture write; answer 'what did I work on' with list_time. " +
            "BILLING: when asked to prepare a bill or pre-bill, use export_prebill (matter, optional date " +
            "range) — it files the PDF on the matter for billing review. " +
            "TASKS: capture to-dos with add_task (matter, title, optional assignee and target date); answer " +
            "'what's open' with list_tasks and close them with complete_task. Hard court dates stay on the " +
            "calendar, not the task list. " +
            "BEFORE opening a matter for a new client or adverse party, run check_conflicts with every " +
            "involved name; after the user decides, freeze the result with attest_conflict_check on the matter " +
            "(record parties with add_matter_party as they become known). " +
            "Use search_clauses / draft_clause for clause work (the firm's own curated library); for a full document " +
            "(an NDA, a consulting agreement) use draft_from_template (see list_document_templates; curate with " +
            "save_document_template). To deliver a draft as " +
            "work product, chain the tools: draft_clause or draft_from_template, then generate_pdf with the drafted text, then " +
            "attach_document_to_matter with the returned file id. When reviewing a contract, first call get_playbook " +
            "and check the document against every rule, citing the file for each finding. " +
            "LIBRARY CURATION: administrators refine the firm's standards from chat — save_clause / remove_clause " +
            "for the clause library and add_playbook_rule / remove_playbook_rule for the review playbook; every " +
            "change immediately affects future drafting and reviews, so confirm the exact wording before saving. " +
            "Always make clear that " +
            "output is a starting template, not legal advice, and recommend review by a licensed attorney. Never " +
            "invent statutes, case citations, or jurisdiction-specific rules; if asked for those, say a qualified " +
            "lawyer must confirm them.",
        // Guided workflows (v1 item 8): each starter drives a packaged multi-step chain the
        // instructions prescribe — review-against-playbook, draft-and-file, matter Q&A with citations.
        SuggestedPrompts =
        [
            "List my matters",
            "Review the attached contract against our playbook and file the memo on the matter",
            "Draft an NDA between our client and the counterparty, and file it on the matter",
            "Summarize the documents on a matter, citing each file",
            "Search the clause library for indemnification",
        ],
        // Chat personas (selectable per turn in the composer's agent picker): each sees ONLY the
        // tools its job needs — selections narrow the caller's RBAC, never widen it. A firm admin
        // can override any of these with a same-named profile under Admin -> Agent Profiles.
        Agents =
        [
            new AgentDescriptor
            {
                Name = "drafter",
                Description = "Contract drafting - clause library, templates, playbook, and filing only.",
                Instructions =
                    "You are the drafting specialist. Work exclusively on contract language: search the " +
                    "clause library, draft from the firm's templates, apply the negotiation playbook, and " +
                    "file finished drafts on the matter (generate_pdf then attach_document_to_matter, " +
                    "which waits for attorney approval). Decline docketing, conflicts, or matter " +
                    "management - point the user at the general assistant.",
                ToolNames =
                [
                    "search_clauses", "draft_clause", "list_document_templates", "draft_from_template",
                    "get_playbook", "generate_pdf", "attach_document_to_matter", "list_matters",
                ],
            },
            new AgentDescriptor
            {
                Name = "docketing",
                Description = "The calendar clerk - matter events and dates, nothing else.",
                Instructions =
                    "You are the docketing clerk. Record and report matter events (court dates, filing " +
                    "deadlines, meetings) with precision; always confirm dates back in ISO format and " +
                    "flag urgency honestly. Nothing else is yours: no drafting, no conflicts, no billing.",
                ToolNames =
                [
                    "add_matter_event", "list_matter_events", "list_upcoming_events", "complete_event", "list_matters",
                ],
            },
        ],
        // A shipped chain: draft the engagement papers, then docket the follow-up dates. Each step
        // runs as a full authorized turn - approvals and audit apply to every step.
        Workflows =
        [
            new WorkflowDescriptor
            {
                Name = "engage-and-docket",
                Description = "Draft the engagement papers, then docket the follow-up dates.",
                AgentNames = ["drafter", "docketing"],
            },
        ],
        Roles = ["legal:user", "legal:admin"],
        // The first five minutes: the shell offers this wizard while the matters list probes
        // empty, to anyone holding legal.manage. Every form step posts to the same manual-CRUD
        // endpoints the tab editors use — same RBAC, same invariants, no separate write path.
        Onboarding = new OnboardingDescriptor
        {
            ProbeEndpoint = "/api/legal/matters",
            Permission = Manage,
            Title = "Set up your practice",
            Steps =
            [
                new OnboardingStep
                {
                    Id = "welcome",
                    Kind = "info",
                    Title = "Welcome to Casewell",
                    Blurb = "Casewell organizes the practice around matters: documents, deadlines, tasks, and time " +
                            "all attach to an engagement, and the assistant works inside it. A few guided minutes " +
                            "now — your first client, matter, and key dates — and every tab has something real on it.",
                },
                new OnboardingStep
                {
                    Id = "firm-basics",
                    Kind = "info",
                    Title = "Your firm's foundations",
                    Blurb = "Already set up for you: a clause library and a contract-review playbook (curate both " +
                            "from their tabs or in chat), and docket numbers assigned automatically as YYYY-NNNN. " +
                            "Invite the team and refine roles under Admin — paralegals get a working baseline out " +
                            "of the box.",
                },
                new OnboardingStep
                {
                    Id = "first-client",
                    Kind = "form",
                    Title = "Add your first client",
                    Blurb = "Who does the firm work for? Clients live in the contact book: matters reference them " +
                            "by name and conflict checks search them.",
                    Endpoint = "/api/legal/clients",
                    Fields =
                    [
                        new("name", "Client name"),
                        new("organization", "Organization (optional)", Required: false),
                        new("email", "Email (optional)", Required: false),
                        new("phone", "Phone (optional)", Required: false),
                    ],
                },
                new OnboardingStep
                {
                    Id = "first-matter",
                    Kind = "form",
                    Title = "Open your first matter",
                    Blurb = "The engagement workspace everything attaches to. It gets the next docket number " +
                            "automatically; name the client you just added so the matter files under them.",
                    Endpoint = "/api/legal/matters",
                    Fields =
                    [
                        new("name", "Matter name (e.g. 'Acme / Initech NDA')"),
                        new("clientName", "Client (optional)", Required: false),
                        new("practiceArea", "Practice area (optional)", Required: false),
                    ],
                },
                new OnboardingStep
                {
                    Id = "key-deadlines",
                    Kind = "form",
                    Title = "Docket the key deadlines",
                    Blurb = "Deadlines are the malpractice surface — get the dates you already know onto the " +
                            "calendar. Docketed dates remind ahead of time and when overdue, and open deadlines " +
                            "block matter close-out.",
                    Endpoint = "/api/legal/events",
                    Preset = new Dictionary<string, string> { ["type"] = "deadline" },
                    Fields =
                    [
                        new("matterName", "Matter"),
                        new("title", "Deadline (e.g. 'Answer due')"),
                        new("startsAt", "When (e.g. 2026-08-14)"),
                    ],
                },
                new OnboardingStep
                {
                    Id = "done",
                    Kind = "info",
                    Title = "What to try first",
                    Blurb = "Ask the assistant to 'brief me on' your new matter, attach a contract and have it " +
                            "reviewed against the playbook, or say 'log half an hour — call with the client' and " +
                            "watch the Time tab. Everything the assistant writes asks for your approval first.",
                },
            ],
        },
        Tools =
        [
            new ToolDescriptor
            {
                Name = "search_clauses",
                Description = "Search the standard clause library by keyword; returns clause titles, categories, and summaries.",
                Permission = Permissions.ForTool(Id, "search_clauses"),
            },
            new ToolDescriptor
            {
                Name = "draft_clause",
                Description = "Draft a standard contract clause filled in with the two party names.",
                Permission = Permissions.ForTool(Id, "draft_clause"),
            },
            new ToolDescriptor
            {
                Name = "save_document_template",
                Description = "Save a reusable document template (ordered clause types assembled by draft_from_template). Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "save_document_template"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_document_templates",
                Description = "List the firm's document templates and the clauses each assembles.",
                Permission = Permissions.ForTool(Id, "list_document_templates"),
            },
            new ToolDescriptor
            {
                Name = "draft_from_template",
                Description = "Assemble a full document draft from a saved template with the party names filled in.",
                Permission = Permissions.ForTool(Id, "draft_from_template"),
            },
            new ToolDescriptor
            {
                Name = "create_matter",
                Description = "Create a legal matter (engagement workspace) with an auto-assigned matter number and optional practice area. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "create_matter"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "set_matter_status",
                Description = "Change a matter's status (open / on-hold / closed). Closing runs a completeness check: open events/tasks block it unless forced after user confirmation. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "set_matter_status"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_matters",
                Description = "List the tenant's matters with status and document counts.",
                Permission = Permissions.ForTool(Id, "list_matters"),
            },
            new ToolDescriptor
            {
                Name = "add_matter_party",
                Description = "Record a party (client / opposing / related) on a matter — the surface conflict checks search. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_matter_party"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "log_time",
                Description = "Log time worked on a matter (billable by default). Quick capture — appends an entry without an approval step.",
                Permission = Permissions.ForTool(Id, "log_time"),
            },
            new ToolDescriptor
            {
                Name = "list_time",
                Description = "List logged time: a matter's entries with billable totals, or the caller's own recent time across matters.",
                Permission = Permissions.ForTool(Id, "list_time"),
            },
            new ToolDescriptor
            {
                Name = "export_prebill",
                Description = "Generate a pre-bill (time entries + billable totals over a period) as a PDF filed on the matter. Side-effecting: writes a document and requires human approval.",
                Permission = Permissions.ForTool(Id, "export_prebill"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "add_task",
                Description = "Add a task (to-do) on a matter, optionally assigned by name with a target date. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_task"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_tasks",
                Description = "List open tasks across all matters or for one matter (dated tasks first).",
                Permission = Permissions.ForTool(Id, "list_tasks"),
            },
            new ToolDescriptor
            {
                Name = "complete_task",
                Description = "Mark a matter task as completed. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "complete_task"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "check_conflicts",
                Description = "Search all matters' parties and clients for conflicts of interest against given names. Read-only; restricted matters register as hits without revealing details.",
                Permission = Permissions.ForTool(Id, "check_conflicts"),
            },
            new ToolDescriptor
            {
                Name = "attest_conflict_check",
                Description = "Freeze a conflict search into the matter's tamper-evident hash-chained attestation record. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "attest_conflict_check"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_conflict_attestations",
                Description = "List a matter's conflict attestations and verify the hash chain's integrity.",
                Permission = Permissions.ForTool(Id, "list_conflict_attestations"),
            },
            new ToolDescriptor
            {
                Name = "attach_document_to_matter",
                Description = "Attach a stored file to a matter by name. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "attach_document_to_matter"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_matter_documents",
                Description = "List a matter's attached documents with their file ids.",
                Permission = Permissions.ForTool(Id, "list_matter_documents"),
            },
            new ToolDescriptor
            {
                Name = "add_matter_event",
                Description = "Add a deadline / hearing / meeting / reminder to a matter's calendar. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_matter_event"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_matter_events",
                Description = "List a matter's calendar events with overdue / due-soon markers.",
                Permission = Permissions.ForTool(Id, "list_matter_events"),
            },
            new ToolDescriptor
            {
                Name = "list_upcoming_events",
                Description = "The firm agenda: upcoming events across accessible matters, overdue first-class.",
                Permission = Permissions.ForTool(Id, "list_upcoming_events"),
            },
            new ToolDescriptor
            {
                Name = "complete_event",
                Description = "Mark a matter's calendar event as completed: it leaves the agenda, stops reminding, and stops blocking close-out. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "complete_event"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "get_matter_overview",
                Description = "One-look matter brief: status, parties, open events, open tasks, time totals, recent documents.",
                Permission = Permissions.ForTool(Id, "get_matter_overview"),
            },
            new ToolDescriptor
            {
                Name = "draft_status_update",
                Description = "Draft a client-facing status letter (progress, upcoming dates, hours - nothing internal) filed on the matter as a PDF draft for attorney review. Side-effecting: writes a document and requires human approval.",
                Permission = Permissions.ForTool(Id, "draft_status_update"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "send_status_update",
                Description = "Email the current status letter to the matter's client — ONLY after an attorney reviewed a draft (ABA Op 512: AI output is reviewed before it leaves the firm). Outward-facing and requires human approval.",
                Permission = Permissions.ForTool(Id, "send_status_update"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "get_playbook",
                Description = "Get the firm's contract-review playbook rules with severity.",
                Permission = Permissions.ForTool(Id, "get_playbook"),
            },
            new ToolDescriptor
            {
                Name = "save_clause",
                Description = "Add or replace a clause in the firm's library (curation). Side-effecting: writes firm standards and requires human approval.",
                Permission = Permissions.ForTool(Id, "save_clause"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "remove_clause",
                Description = "Remove a clause from the firm's library; refused while a document template references it. Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "remove_clause"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "add_playbook_rule",
                Description = "Add a rule to the firm's contract-review playbook. Side-effecting: changes the review standard and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_playbook_rule"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "remove_playbook_rule",
                Description = "Remove a playbook rule by title. Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "remove_playbook_rule"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "start_bulk_review",
                Description = "Start a background bulk review of all documents on a matter against a set of questions. Side-effecting: consumes resources and files a report, requires human approval.",
                Permission = Permissions.ForTool(Id, "start_bulk_review"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "index_matter_documents",
                Description = "Index a matter's documents into its searchable knowledge collection (for search_knowledge). Side-effecting: consumes resources, requires human approval.",
                Permission = Permissions.ForTool(Id, "index_matter_documents"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "restrict_matter_access",
                Description = "Put a matter behind an ethical wall (only the caller keeps access). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "restrict_matter_access"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "open_matter_access",
                Description = "Lift a matter's ethical wall (caller must be inside it). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "open_matter_access"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "connect_matter_folder",
                Description = "Bind a matter to a folder in a connected data source and start syncing (files attach + index automatically). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "connect_matter_folder"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "sync_matter_folder",
                Description = "Re-sync a matter's bound folder (new/changed files attach + index; unchanged skipped). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "sync_matter_folder"),
                RequiresApproval = true,
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/legal/chat", Icon = "message-circle", Order = 0 },
            new TabDescriptor
            {
                Id = "matters", Label = "Matters", Route = "/legal/matters", Icon = "folder", Order = 1,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/matters",
                DetailEndpoint = "/api/legal/matters/{id}/detail",
                Placeholder = "No matters yet. Matters are opened in Chat - try: 'Open a matter for Acme / Initech NDA, client Acme LLC'. The assistant runs the conflict check first and asks for your approval before anything is created.",
                Columns =
                [
                    new("matterNumber", "Number"), new("name", "Matter"), new("clientName", "Client"),
                    new("practiceArea", "Practice area"), new("status", "Status"),
                    new("documentCount", "Documents"), new("createdAt", "Opened"),
                ],
                // Hand-editing an engagement is a human act, not an agent proposal: RBAC only, no
                // approval gate. The endpoint still runs the close-out check (no force from a
                // form) and only history-free matters delete — closing is the paper trail.
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/matters",
                    DeleteEndpoint = "/api/legal/matters/{id}",
                    Permission = Manage,
                    KeyField = "name",
                    Fields =
                    [
                        new("name", "Matter name"),
                        new("clientName", "Client (optional)", Required: false),
                        new("clientEmail", "Client email for status letters (optional)", Required: false),
                        new("practiceArea", "Practice area (optional)", Required: false),
                        new("status", "Status (open / on-hold / closed)", Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "clients", Label = "Clients", Route = "/legal/clients", Icon = "users", Order = 2,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/clients",
                Placeholder = "No clients yet. Add the people and companies the firm works for - matters reference them by name, and conflict checks search this book.",
                Columns =
                [
                    new("name", "Client"), new("organization", "Organization"), new("email", "Email"),
                    new("phone", "Phone"), new("matters", "Matters"),
                ],
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/clients",
                    DeleteEndpoint = "/api/legal/clients/{id}",
                    Permission = Manage,
                    KeyField = "name",
                    Fields =
                    [
                        new("name", "Client name"),
                        new("organization", "Organization (optional)", Required: false),
                        new("email", "Email (optional)", Required: false),
                        new("phone", "Phone (optional)", Required: false),
                        new("notes", "Notes (optional)", Multiline: true, Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "calendar", Label = "Calendar", Route = "/legal/calendar", Icon = "calendar", Order = 3,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/events",
                Placeholder = "Nothing docketed yet. Deadlines are captured in Chat - try: 'The answer in Acme is due August 14'. Docketed dates remind ahead of time and when overdue.",
                Columns =
                [
                    new("startsAt", "When"), new("type", "Type"), new("title", "Event"),
                    new("matterName", "Matter"), new("urgency", "Urgency"),
                ],
                // The id is the row identity (blank on add, read-only on edit) - the Networthy
                // categories pattern. Completing an event stays a chat act (complete_event).
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/events",
                    DeleteEndpoint = "/api/legal/events/{id}",
                    Permission = Manage,
                    KeyField = "id",
                    Fields =
                    [
                        new("id", "Id (leave blank when adding)", Required: false),
                        new("matterName", "Matter"),
                        new("title", "Event"),
                        new("type", "Type (deadline / hearing / meeting / reminder)", Required: false),
                        new("startsAt", "When (e.g. 2026-08-14 or 2026-08-14 09:00)", Required: false),
                        new("notes", "Notes (optional)", Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "tasks", Label = "Tasks", Route = "/legal/tasks", Icon = "check-square", Order = 4,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/tasks",
                Placeholder = "No open tasks. To-dos are captured in Chat ('add a task on Acme: draft the motion') or added here by hand.",
                Columns =
                [
                    new("matterName", "Matter"), new("title", "Task"), new("assignedTo", "Assigned to"),
                    new("dueOn", "Due"), new("status", "Status"),
                ],
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/tasks",
                    DeleteEndpoint = "/api/legal/tasks/{id}",
                    Permission = Manage,
                    KeyField = "id",
                    Fields =
                    [
                        new("id", "Id (leave blank when adding)", Required: false),
                        new("matterName", "Matter"),
                        new("title", "Task"),
                        new("assignedTo", "Assigned to (optional)", Required: false),
                        new("dueOn", "Due (e.g. 2026-08-01, optional)", Required: false),
                        new("status", "Status (open / done)", Required: false),
                        new("notes", "Notes (optional)", Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "time", Label = "Time", Route = "/legal/time", Icon = "clock", Order = 5,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/time-entries",
                Placeholder = "No time on the books. Capture it in Chat ('log half an hour on Acme - call with opposing counsel') or add entries here by hand.",
                Columns =
                [
                    new("workedOn", "Day"), new("matterName", "Matter"), new("who", "Who"),
                    new("hours", "Hours"), new("billable", "Billable"), new("description", "Narrative"),
                ],
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/time-entries",
                    DeleteEndpoint = "/api/legal/time-entries/{id}",
                    Permission = Manage,
                    KeyField = "id",
                    Fields =
                    [
                        new("id", "Id (leave blank when adding)", Required: false),
                        new("matterName", "Matter"),
                        new("hours", "Hours (e.g. 0.5)", Numeric: true),
                        new("description", "Narrative (becomes the line on the bill)"),
                        new("workedOn", "Day (e.g. 2026-07-10, optional = today)", Required: false),
                        new("billable", "Billable (yes / no, default yes)", Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "hours", Label = "Hours", Route = "/legal/hours", Icon = "trending-up", Order = 6,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/time/weekly",
                // The platform renders these rows as a time-series line chart of the firm's
                // billable hours per week (Mondays on the x axis).
                Chart = new TabChart { XField = "weekOf", YField = "hours", YLabel = "Billable hours" },
                Placeholder = "The trend appears once time is on the books - log entries in Chat or on the Time tab.",
            },
            new TabDescriptor
            {
                Id = "clauses", Label = "Clauses", Route = "/legal/clauses", Icon = "file-text", Order = 7,
                Permission = ViewClauses,
                DataEndpoint = "/api/legal/clauses",
                Columns = [new("title", "Clause"), new("category", "Category"), new("summary", "Summary")],
                // Librarians edit the firm's precedent right in the table (permission-gated both
                // in the payload and on the endpoints); everyone else sees it read-only.
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/clauses",
                    DeleteEndpoint = "/api/legal/clauses/{slug}",
                    Permission = ManageLibrary,
                    KeyField = "slug",
                    Fields =
                    [
                        new("slug", "Type (stable id, e.g. data-protection)"),
                        new("title", "Title"),
                        new("category", "Category"),
                        new("summary", "Summary"),
                        new("template", "Clause text ({PartyA}/{PartyB} placeholders)", Multiline: true),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "playbook", Label = "Playbook", Route = "/legal/playbook", Icon = "shield-check", Order = 8,
                Permission = ViewClauses,
                DataEndpoint = "/api/legal/playbook",
                Columns = [new("severity", "Severity"), new("title", "Rule"), new("guidance", "Guidance")],
                // Rules are add/delete (the endpoint has no upsert identity) - edit = remove + add.
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/legal/playbook",
                    DeleteEndpoint = "/api/legal/playbook/{id}",
                    Permission = ManageLibrary,
                    Fields =
                    [
                        new("title", "Rule"),
                        new("guidance", "Guidance (what to check, what to flag)", Multiline: true),
                        new("severity", "Severity (info / caution / critical)"),
                    ],
                },
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<LegalTools>();
        services.AddScoped<MatterTools>();
        services.AddScoped<ConflictTools>();
        services.AddScoped<CalendarTools>();
        services.AddScoped<TimeTools>();
        services.AddScoped<TaskTools>();
        services.AddScoped<BriefingTools>();
        services.AddHostedService<DeadlineReminderService>();
        services.AddSingleton<IModuleToolSource, LegalToolSource>();
        services.AddSingleton<Cortex.Application.Jobs.IJobHandler, BulkReviewJobHandler>();

        // A matter's RAG collection is gated by the matter itself (wall included) — scope-first
        // retrieval. Registered unconditionally; it only ever runs when RAG is enabled.
        services.AddScoped<Cortex.Application.Rag.IRagCollectionGate, MatterRagGate>();

        // Connector sync lands here: synced files attach to the bound matter and index into its
        // knowledge collection (the connect_matter_folder / sync_matter_folder chain).
        services.AddScoped<Cortex.Application.Connectors.IConnectorSyncHandler, MatterSyncHandler>();

        // The module owns its data under the 'legal' schema of the platform database.
        services.AddDbContext<LegalDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(LegalDbContext.ConnectionName)));
    }

    public async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<LegalDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds the tenant's clause library and playbook from the built-in defaults, so drafting and
    /// review work out of the box. Tenant-owned data: only seeds when an ambient tenant is present
    /// (the dev tenant in Development), and only into an empty library — a firm's curation is never
    /// overwritten.
    /// </summary>
    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tenant = services.GetRequiredService<Cortex.Core.Multitenancy.ITenantContext>();
        if (!tenant.HasTenant)
        {
            return;
        }

        var db = services.GetRequiredService<LegalDbContext>();
        var tenantId = tenant.RequireTenantId();

        if (!await db.Clauses.AnyAsync(cancellationToken))
        {
            foreach (var clause in LegalCatalog.Clauses)
            {
                db.Clauses.Add(new TenantClause
                {
                    TenantId = tenantId,
                    Slug = clause.Id,
                    Title = clause.Title,
                    Category = clause.Category,
                    Summary = clause.Summary,
                    Template = clause.Template,
                });
            }
        }

        if (!await db.DocumentTemplates.AnyAsync(cancellationToken))
        {
            // One working template out of the box, so draft_from_template demos immediately; the
            // clause slugs reference the seeded library above.
            db.DocumentTemplates.Add(new DocumentTemplate
            {
                TenantId = tenantId,
                Name = "mutual-nda",
                Title = "Mutual Non-Disclosure Agreement between {PartyA} and {PartyB}",
                ClauseSlugsJson = """["confidentiality","termination","governing-law"]""",
            });
        }

        if (!await db.PlaybookRules.AnyAsync(cancellationToken))
        {
            foreach (var (title, guidance, severity) in LegalCatalog.DefaultPlaybook)
            {
                db.PlaybookRules.Add(new PlaybookRule
                {
                    TenantId = tenantId,
                    Title = title,
                    Guidance = guidance,
                    Severity = severity,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/legal").WithTags("Legal").RequireAuthorization();

        // The hand-edit surface (tab editors + setup wizard): clients, matters, deadlines,
        // tasks, time entries, and the weekly-hours chart rows. RBAC-gated, never approval-gated.
        group.MapManualCrudEndpoints();

        // The tenant's clause library (seeded from defaults, curated by the firm).
        group.MapGet("/clauses", async (string? query, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var clauses = await db.Clauses.OrderBy(c => c.Title).Take(500).ToListAsync(cancellationToken);
                var selected = string.IsNullOrWhiteSpace(query)
                    ? clauses
                    : LegalCatalog.Search(clauses, query, c => [c.Title, c.Category, c.Summary, c.Slug]);
                return Results.Ok(selected.Select(c => new ClauseDto(c.Id, c.Slug, c.Title, c.Category, c.Summary, c.Template)));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewClauses))
            .WithName("Legal_GetClauses");

        // Curate the clause library.
        group.MapPost("/clauses", async (
                UpsertClauseRequest body, LegalDbContext db,
                Cortex.Core.Multitenancy.ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var existing = await db.Clauses.FirstOrDefaultAsync(c => c.Slug == body.Slug, cancellationToken);
                if (existing is null)
                {
                    existing = new TenantClause
                    {
                        TenantId = tenant.RequireTenantId(),
                        Slug = body.Slug,
                        Title = body.Title,
                        Category = body.Category,
                        Summary = body.Summary,
                        Template = body.Template,
                    };
                    db.Clauses.Add(existing);
                }
                else
                {
                    existing.Title = body.Title;
                    existing.Category = body.Category;
                    existing.Summary = body.Summary;
                    existing.Template = body.Template;
                }

                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new ClauseDto(existing.Id, existing.Slug, existing.Title, existing.Category, existing.Summary, existing.Template));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_UpsertClause");

        group.MapDelete("/clauses/{slug}", async (string slug, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var clause = await db.Clauses.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken);
                if (clause is null)
                {
                    return Results.NotFound();
                }

                db.Clauses.Remove(clause);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_DeleteClause");

        // The firm playbook — drives the Playbook tab and the get_playbook tool.
        group.MapGet("/playbook", async (LegalDbContext db, CancellationToken cancellationToken) =>
            {
                // Severity persists as a string (readable rows) — order in memory where enum order applies.
                var rules = (await db.PlaybookRules.ToListAsync(cancellationToken))
                    .OrderByDescending(r => r.Severity)
                    .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new PlaybookRuleDto(r.Id, r.Severity.ToString(), r.Title, r.Guidance));
                return Results.Ok(rules);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewClauses))
            .WithName("Legal_GetPlaybook");

        group.MapPost("/playbook", async (
                UpsertPlaybookRuleRequest body, LegalDbContext db,
                Cortex.Core.Multitenancy.ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var rule = new PlaybookRule
                {
                    TenantId = tenant.RequireTenantId(),
                    Title = body.Title,
                    Guidance = body.Guidance,
                    Severity = body.Severity,
                };
                db.PlaybookRules.Add(rule);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Created($"/api/legal/playbook/{rule.Id}", new PlaybookRuleDto(rule.Id, rule.Severity.ToString(), rule.Title, rule.Guidance));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_AddPlaybookRule");

        group.MapDelete("/playbook/{id:guid}", async (Guid id, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var rule = await db.PlaybookRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
                if (rule is null)
                {
                    return Results.NotFound();
                }

                db.PlaybookRules.Remove(rule);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_DeletePlaybookRule");

        // The tenant's matters — drives the Matters tab (query filter scopes rows to the tenant;
        // the ethical wall filters per user, so a walled matter never renders for outsiders).
        group.MapGet("/matters", async (
                LegalDbContext db, Cortex.Core.Identity.ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var matters = (await db.Matters
                        .OrderByDescending(m => m.CreatedAt)
                        .Take(200)
                        .Select(m => new
                        {
                            m.Id, m.Name, m.MatterNumber, m.ClientName, m.PracticeArea, m.Status,
                            m.RestrictedUserIdsJson, DocumentCount = m.Documents.Count, m.CreatedAt,
                        })
                        .ToListAsync(cancellationToken))
                    .Where(m => Matter.WallAllows(m.RestrictedUserIdsJson, current.UserId))
                    .Select(m => new MatterDto(
                        m.Id, m.MatterNumber, m.Name, m.ClientName, m.PracticeArea, m.Status.ToString(),
                        m.DocumentCount, DateOnly.FromDateTime(m.CreatedAt.UtcDateTime)));
                return Results.Ok(matters);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetMatters");

        // The firm agenda — drives the Calendar tab. Wall-filtered like the matters list; urgency
        // is computed server-side so the tab needs no client logic.
        group.MapGet("/events", async (
                LegalDbContext db, Cortex.Core.Identity.ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var now = DateTimeOffset.UtcNow;
                var matters = (await db.Matters
                        .Select(m => new { m.Id, m.Name, m.RestrictedUserIdsJson })
                        .ToListAsync(cancellationToken))
                    .Where(m => Matter.WallAllows(m.RestrictedUserIdsJson, current.UserId))
                    .ToDictionary(m => m.Id, m => m.Name);
                var events = (await db.MatterEvents
                        .OrderBy(e => e.StartsAt)
                        .Take(500)
                        .ToListAsync(cancellationToken))
                    .Where(e => matters.ContainsKey(e.MatterId))
                    .Select(e => new MatterEventDto(
                        e.Id, e.StartsAt, e.Type, e.Title, matters[e.MatterId],
                        MatterEvent.UrgencyAt(now, e.StartsAt).ToString(), e.Notes));
                return Results.Ok(events);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetEvents");

        // A matter's attached documents (file ids resolve against /api/files/{id}). Outside the
        // wall, the matter 404s — indistinguishable from missing, like cross-tenant ids.
        // The matter's working file as a generic DETAIL DOCUMENT (drill-down from the Matters tab):
        // parties, upcoming events with urgency, the tamper-evident conflict-check trail, and
        // documents. Outside the wall it 404s - indistinguishable from missing.
        group.MapGet("/matters/{matterId:guid}/detail", async (
                Guid matterId, LegalDbContext db, Cortex.Core.Identity.ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == matterId, cancellationToken);
                if (matter is null || !matter.IsAccessibleTo(current.UserId))
                {
                    return Results.NotFound();
                }

                var now = DateTimeOffset.UtcNow;
                var parties = await db.MatterParties.Where(p => p.MatterId == matterId)
                    .OrderBy(p => p.Role).ThenBy(p => p.Name).Take(50).ToListAsync(cancellationToken);
                var events = await db.MatterEvents.Where(e => e.MatterId == matterId && e.StartsAt >= now.AddDays(-7))
                    .OrderBy(e => e.StartsAt).Take(20).ToListAsync(cancellationToken);
                var attestations = await db.ConflictAttestations.Where(a => a.MatterId == matterId)
                    .OrderByDescending(a => a.PerformedAt).Take(10).ToListAsync(cancellationToken);
                var documents = await db.MatterDocuments.Where(d => d.MatterId == matterId)
                    .OrderByDescending(d => d.CreatedAt).Take(20).ToListAsync(cancellationToken);

                var sections = new List<object>
                {
                    new
                    {
                        heading = "Parties",
                        columns = new[] { new { field = "name", header = "Name" }, new { field = "role", header = "Role" } },
                        rows = (object)parties.Select(p => new { name = p.Name, role = p.Role }).ToArray(),
                    },
                    new
                    {
                        heading = "Calendar (last week and upcoming)",
                        columns = new[]
                        {
                            new { field = "startsAt", header = "When" }, new { field = "type", header = "Type" },
                            new { field = "title", header = "Event" }, new { field = "urgency", header = "Urgency" },
                        },
                        rows = (object)events.Select(e => new
                        {
                            startsAt = e.StartsAt.ToString("yyyy-MM-dd HH:mm"),
                            type = e.Type,
                            title = e.Title,
                            urgency = MatterEvent.UrgencyAt(now, e.StartsAt).ToString(),
                        }).ToArray(),
                    },
                    new
                    {
                        heading = "Conflict checks (tamper-evident trail)",
                        columns = new[]
                        {
                            new { field = "performedAt", header = "When" }, new { field = "terms", header = "Searched" },
                            new { field = "hash", header = "Attestation" },
                        },
                        rows = (object)attestations.Select(a => new
                        {
                            performedAt = a.PerformedAt.ToString("yyyy-MM-dd HH:mm"),
                            terms = string.Join(", ", System.Text.Json.JsonSerializer.Deserialize<string[]>(a.SearchTermsJson) ?? []),
                            hash = a.AttestationHash[..Math.Min(12, a.AttestationHash.Length)] + "...",
                        }).ToArray(),
                    },
                    new
                    {
                        heading = "Documents",
                        columns = new[]
                        {
                            new { field = "fileName", header = "File" }, new { field = "note", header = "Note" },
                            new { field = "attachedAt", header = "Attached" },
                        },
                        rows = (object)documents.Select(d => new
                        {
                            fileName = d.FileName, note = d.Note, attachedAt = d.CreatedAt.ToString("yyyy-MM-dd"),
                        }).ToArray(),
                    },
                };

                return Results.Ok(new
                {
                    title = $"{(matter.MatterNumber is null ? "" : matter.MatterNumber + " - ")}{matter.Name}",
                    subtitle = $"{matter.Status}{(matter.ClientName is null ? "" : $" - Client: {matter.ClientName}")}" +
                               $"{(matter.PracticeArea is null ? "" : $" - {matter.PracticeArea}")}" +
                               (matter.RestrictedUserIdsJson is null ? "" : " - RESTRICTED (ethical wall)"),
                    sections,
                });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetMatterDetail");

        group.MapGet("/matters/{matterId:guid}/documents", async (
                Guid matterId, LegalDbContext db, Cortex.Core.Identity.ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == matterId, cancellationToken);
                if (matter is null || !matter.IsAccessibleTo(current.UserId))
                {
                    return Results.NotFound();
                }

                var documents = await db.MatterDocuments
                    .Where(d => d.MatterId == matterId)
                    .OrderByDescending(d => d.CreatedAt)
                    .Select(d => new MatterDocumentDto(d.FileId, d.FileName, d.Note, d.CreatedAt))
                    .ToListAsync(cancellationToken);
                return Results.Ok(documents);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetMatterDocuments");
    }

    private sealed record MatterDto(
        Guid Id, string? MatterNumber, string Name, string? ClientName, string? PracticeArea,
        string Status, int DocumentCount, DateOnly CreatedAt);

    private sealed record MatterDocumentDto(Guid FileId, string FileName, string? Note, DateTimeOffset AttachedAt);

    private sealed record MatterEventDto(
        Guid Id, DateTimeOffset StartsAt, string Type, string Title, string MatterName, string Urgency, string? Notes);

    private sealed record ClauseDto(Guid Id, string Slug, string Title, string Category, string Summary, string Template);

    /// <summary>Create or update a clause in the firm's library (matched by slug).</summary>
    public sealed record UpsertClauseRequest(string Slug, string Title, string Category, string Summary, string Template);

    private sealed record PlaybookRuleDto(Guid Id, string Severity, string Title, string Guidance);

    /// <summary>Add a rule to the firm's contract-review playbook.</summary>
    public sealed record UpsertPlaybookRuleRequest(string Title, string Guidance, RuleSeverity Severity = RuleSeverity.Caution);
}
