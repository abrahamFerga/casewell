using Cortex.Application.Authorization;
using Cortex.Application.Commerce;
using Cortex.AspNetCore.Connectors;
using Cortex.AspNetCore.Hosting;
using Cortex.AspNetCore.Modules;
using Cortex.Connectors.AzureBlob;
using Cortex.Connectors.GoogleDrive;
using Cortex.Connectors.LocalFolder;
using Cortex.Connectors.MsGraph;
using Cortex.Connectors.Peer;
using Cortex.Connectors.S3;
using Cortex.Connectors.Documenso;
using Cortex.Modules.Legal;
using Casewell.Host;

// ─────────────────────────────────────────────────────────────────────────────
// Casewell — a single-vertical legal system built ENTIRELY on the Cortex
// platform. The platform packages supply auth (OIDC / Entra External ID-ready),
// multi-tenancy, RBAC, append-only audit, chat over AG-UI + SignalR, document
// tools, the permission-aware RAG pipeline, background jobs, connectors, and
// the admin API; this host adds only the legal module and product composition.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddCortexPlatform();

builder.AddCortexModule<LegalModule>();

// Data-source connectors (default-off per tenant; enabled in the admin console):
builder.AddCortexConnector<LocalFolderConnector>();
builder.AddCortexConnector<AzureBlobConnector>();
builder.AddCortexConnector<MsGraphConnector>();    // "the firm keeps its files in Microsoft 365"
builder.AddCortexConnector<GoogleDriveConnector>(); // …or in Google Drive
builder.AddCortexConnector<S3Connector>();         // …or in an S3 bucket (AWS, MinIO, R2)
builder.AddCortexConnector<DocumensoConnector>(); // e-signature (open-source; hosted or self-hosted)
builder.AddCortexConnector<CortexPeerConnector>(); // talk to sibling Cortex systems

// What this product sells (the plan — not checkout metadata — decides what a purchase grants).
builder.Services.AddCortexProduct(new ProductOffering
{
    ProductId = "casewell",
    Plans =
    [
        new ProductPlan { Id = "solo", Modules = ["legal"], DefaultSeats = 1, MonthlyTokenBudget = 200_000 },
        new ProductPlan { Id = "team", Modules = ["legal"], DefaultSeats = 5, MonthlyTokenBudget = 500_000 },
        new ProductPlan { Id = "dedicated", Dedicated = true },
    ],
});

// The firm's operating role (SPEC.md's RBAC model, the Networthy household-admin analog):
// every legal tool plus the hand-edit surface — tab editors and the setup wizard run behind
// legal.manage with no AI approval gate, because a person on a form is acting directly.
// Seeded into every tenant's editable baseline; firm admins refine it per tenant afterwards.
builder.Services.AddCortexRole("firm-admin",
[
    "chat.use", "chat.conversations.view", "files.upload", "files.read",
    "tools.documents.read_document", "tools.documents.list_documents",
    "tools.legal.*",
    LegalModule.ViewMatters, LegalModule.ViewClauses, LegalModule.ManageLibrary, LegalModule.Manage,
]);

// A law-office role between guest and user: paralegals work matters, the docket, and the
// library — but never attest conflicts, restrict access, or run billing (and deliberately
// not legal.manage: hand-editing the record is the firm admin's surface). Seeded into every
// tenant's editable baseline; firm admins refine it per tenant afterwards.
builder.Services.AddCortexRole("paralegal",
[
    "chat.use", "chat.conversations.view", "files.upload", "files.read",
    "tools.documents.read_document", "tools.documents.list_documents",
    "tools.legal.list_matters",
        "tools.legal.log_time",
        "tools.legal.list_time",
        "tools.legal.add_task",
        "tools.legal.list_tasks",
        "tools.legal.complete_task",
        "tools.legal.get_matter_overview",
        "tools.legal.draft_status_update", "tools.legal.list_matter_documents",
    "tools.legal.add_matter_event",
        "tools.legal.complete_event", "tools.legal.list_matter_events", "tools.legal.list_upcoming_events",
    "tools.legal.search_clauses", "tools.legal.list_document_templates",
    "legal.matters.view", "legal.clauses.view",
]);

// After any tenant is provisioned (operator call or billing webhook): welcome the admin.
builder.Services.AddCortexTenantProvisionedHook<WelcomeEmailHook>();

var app = builder.Build();

await app.RunCortexPlatformAsync();

/// <summary>Exposed so integration tests can host this app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
