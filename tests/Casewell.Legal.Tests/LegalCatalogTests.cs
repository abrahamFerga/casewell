using Cortex.Modules.Legal;

namespace Cortex.Modules.Legal.Tests;

public sealed class LegalCatalogTests
{
    [Fact]
    public void Search_IsCaseInsensitive_AndMatchesCategoryAndSummary()
    {
        Assert.Contains(LegalCatalog.Search("CONFIDENTIAL"), c => c.Id == "confidentiality");
        // "Risk allocation" is a category shared by indemnification + limitation-of-liability.
        Assert.True(LegalCatalog.Search("risk").Count() >= 2);
    }

    [Fact]
    public void Search_BlankQuery_ReturnsNothing()
    {
        Assert.Empty(LegalCatalog.Search("   "));
    }

    [Fact]
    public void Render_SubstitutesBothParties()
    {
        var clause = LegalCatalog.Search("indemnification").First();

        var rendered = LegalCatalog.Render(clause, "Acme Corp", "Beta LLC");

        Assert.Contains("Acme Corp", rendered.Body, StringComparison.Ordinal);
        Assert.Contains("Beta LLC", rendered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{PartyA}", rendered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{PartyB}", rendered.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_BlankParties_FallBackToPlaceholders()
    {
        var clause = LegalCatalog.Search("governing").First();

        var rendered = LegalCatalog.Render(clause, "", "  ");

        Assert.Contains("Party A", rendered.Body, StringComparison.Ordinal);
        Assert.Contains("Party B", rendered.Body, StringComparison.Ordinal);
    }

    // DraftClause / SearchClauses became DB-backed (the tenant's curated library) — their behavior
    // is covered end-to-end in Cortex.Sample.Host.IntegrationTests.LegalLibraryTests.

    [Fact]
    public void DefaultPlaybook_HasRulesAtEverySeverity()
    {
        var severities = LegalCatalog.DefaultPlaybook.Select(r => r.Severity).Distinct().ToList();
        Assert.Contains(Persistence.RuleSeverity.Critical, severities);
        Assert.Contains(Persistence.RuleSeverity.Caution, severities);
        Assert.Contains(Persistence.RuleSeverity.Info, severities);
    }

    [Fact]
    public void Manifest_DeclaresClauseAndMatterToolsWithApprovalGatedWrites()
    {
        var manifest = new LegalModule().Manifest;

        Assert.Equal("legal", manifest.Id);
        Assert.Equal(
            ["search_clauses", "draft_clause", "save_document_template", "list_document_templates", "draft_from_template", "create_matter", "set_matter_status", "list_matters", "add_matter_party", "log_time", "list_time", "export_prebill", "add_task", "list_tasks", "complete_task", "check_conflicts", "attest_conflict_check", "list_conflict_attestations", "attach_document_to_matter", "list_matter_documents", "add_matter_event", "list_matter_events", "list_upcoming_events", "complete_event", "get_matter_overview", "draft_status_update", "send_status_update", "get_playbook", "save_clause", "remove_clause", "add_playbook_rule", "remove_playbook_rule", "start_bulk_review", "index_matter_documents", "restrict_matter_access", "open_matter_access", "connect_matter_folder", "sync_matter_folder"],
            manifest.Tools.Select(t => t.Name));

        // The side-effecting matter tools are held for human approval; the read tools are not.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "create_matter" or "set_matter_status" or "add_matter_party"
                or "attest_conflict_check" or "attach_document_to_matter" or "start_bulk_review"
                or "index_matter_documents" or "restrict_matter_access" or "open_matter_access"
                or "connect_matter_folder" or "sync_matter_folder"
                or "save_clause" or "remove_clause" or "add_playbook_rule" or "remove_playbook_rule"
                or "export_prebill" or "add_task" or "complete_task" or "complete_event"
                or "draft_status_update" or "send_status_update"),
            t => Assert.True(t.RequiresApproval));
        // log_time is the module's ONE deliberately non-gated write (quick capture); reads are never gated.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "list_matters" or "list_matter_documents" or "log_time" or "list_time" or "list_tasks"
                or "get_matter_overview"),
            t => Assert.False(t.RequiresApproval));

        Assert.Contains(manifest.Tabs, t => t.Id == "chat");
        // The Matters tab is live: server-driven data, no placeholder needed.
        var mattersTab = manifest.Tabs.First(t => t.Id == "matters");
        Assert.Equal("/api/legal/matters", mattersTab.DataEndpoint);
    }
}
