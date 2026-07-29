using Cortex.Application.Authorization;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Modules.Legal;

/// <summary>Supplies the Legal module's executable tools.</summary>
public sealed class LegalToolSource : IModuleToolSource
{
    public string ModuleId => LegalModule.Id;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var clauses = scopedServices.GetRequiredService<LegalTools>();
        var matters = scopedServices.GetRequiredService<MatterTools>();
        var conflicts = scopedServices.GetRequiredService<ConflictTools>();
        var calendar = scopedServices.GetRequiredService<CalendarTools>();
        var time = scopedServices.GetRequiredService<TimeTools>();
        var tasks = scopedServices.GetRequiredService<TaskTools>();
        var briefing = scopedServices.GetRequiredService<BriefingTools>();

        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "log_time",
                Permission = Permissions.ForTool(ModuleId, "log_time"),
                Function = AIFunctionFactory.Create(time.LogTime, name: "log_time"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_time",
                Permission = Permissions.ForTool(ModuleId, "list_time"),
                Function = AIFunctionFactory.Create(time.ListTime, name: "list_time"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "export_prebill",
                Permission = Permissions.ForTool(ModuleId, "export_prebill"),
                Function = AIFunctionFactory.Create(time.ExportPrebill, name: "export_prebill"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "complete_event",
                Permission = Permissions.ForTool(ModuleId, "complete_event"),
                Function = AIFunctionFactory.Create(calendar.CompleteEvent, name: "complete_event"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "add_task",
                Permission = Permissions.ForTool(ModuleId, "add_task"),
                Function = AIFunctionFactory.Create(tasks.AddTask, name: "add_task"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_tasks",
                Permission = Permissions.ForTool(ModuleId, "list_tasks"),
                Function = AIFunctionFactory.Create(tasks.ListTasks, name: "list_tasks"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "complete_task",
                Permission = Permissions.ForTool(ModuleId, "complete_task"),
                Function = AIFunctionFactory.Create(tasks.CompleteTask, name: "complete_task"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_matter_overview",
                Permission = Permissions.ForTool(ModuleId, "get_matter_overview"),
                Function = AIFunctionFactory.Create(briefing.GetMatterOverview, name: "get_matter_overview"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "draft_status_update",
                Permission = Permissions.ForTool(ModuleId, "draft_status_update"),
                Function = AIFunctionFactory.Create(briefing.DraftStatusUpdate, name: "draft_status_update"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "send_status_update",
                Permission = Permissions.ForTool(ModuleId, "send_status_update"),
                Function = AIFunctionFactory.Create(briefing.SendStatusUpdate, name: "send_status_update"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "search_clauses",
                Permission = Permissions.ForTool(ModuleId, "search_clauses"),
                Function = AIFunctionFactory.Create(clauses.SearchClauses, name: "search_clauses"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "draft_clause",
                Permission = Permissions.ForTool(ModuleId, "draft_clause"),
                Function = AIFunctionFactory.Create(clauses.DraftClause, name: "draft_clause"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "save_document_template",
                Permission = Permissions.ForTool(ModuleId, "save_document_template"),
                Function = AIFunctionFactory.Create(clauses.SaveDocumentTemplate, name: "save_document_template"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_document_templates",
                Permission = Permissions.ForTool(ModuleId, "list_document_templates"),
                Function = AIFunctionFactory.Create(clauses.ListDocumentTemplates, name: "list_document_templates"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "draft_from_template",
                Permission = Permissions.ForTool(ModuleId, "draft_from_template"),
                Function = AIFunctionFactory.Create(clauses.DraftFromTemplate, name: "draft_from_template"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "create_matter",
                Permission = Permissions.ForTool(ModuleId, "create_matter"),
                Function = AIFunctionFactory.Create(matters.CreateMatter, name: "create_matter"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "set_matter_status",
                Permission = Permissions.ForTool(ModuleId, "set_matter_status"),
                Function = AIFunctionFactory.Create(matters.SetMatterStatus, name: "set_matter_status"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_matters",
                Permission = Permissions.ForTool(ModuleId, "list_matters"),
                Function = AIFunctionFactory.Create(matters.ListMatters, name: "list_matters"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "add_matter_party",
                Permission = Permissions.ForTool(ModuleId, "add_matter_party"),
                Function = AIFunctionFactory.Create(conflicts.AddMatterParty, name: "add_matter_party"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "check_conflicts",
                Permission = Permissions.ForTool(ModuleId, "check_conflicts"),
                Function = AIFunctionFactory.Create(conflicts.CheckConflicts, name: "check_conflicts"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "attest_conflict_check",
                Permission = Permissions.ForTool(ModuleId, "attest_conflict_check"),
                Function = AIFunctionFactory.Create(conflicts.AttestConflictCheck, name: "attest_conflict_check"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_conflict_attestations",
                Permission = Permissions.ForTool(ModuleId, "list_conflict_attestations"),
                Function = AIFunctionFactory.Create(conflicts.ListConflictAttestations, name: "list_conflict_attestations"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "attach_document_to_matter",
                Permission = Permissions.ForTool(ModuleId, "attach_document_to_matter"),
                Function = AIFunctionFactory.Create(matters.AttachDocumentToMatter, name: "attach_document_to_matter"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_matter_documents",
                Permission = Permissions.ForTool(ModuleId, "list_matter_documents"),
                Function = AIFunctionFactory.Create(matters.ListMatterDocuments, name: "list_matter_documents"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "add_matter_event",
                Permission = Permissions.ForTool(ModuleId, "add_matter_event"),
                Function = AIFunctionFactory.Create(calendar.AddMatterEvent, name: "add_matter_event"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_matter_events",
                Permission = Permissions.ForTool(ModuleId, "list_matter_events"),
                Function = AIFunctionFactory.Create(calendar.ListMatterEvents, name: "list_matter_events"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_upcoming_events",
                Permission = Permissions.ForTool(ModuleId, "list_upcoming_events"),
                Function = AIFunctionFactory.Create(calendar.ListUpcomingEvents, name: "list_upcoming_events"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "get_playbook",
                Permission = Permissions.ForTool(ModuleId, "get_playbook"),
                Function = AIFunctionFactory.Create(clauses.GetPlaybook, name: "get_playbook"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "save_clause",
                Permission = Permissions.ForTool(ModuleId, "save_clause"),
                Function = AIFunctionFactory.Create(clauses.SaveClause, name: "save_clause"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "remove_clause",
                Permission = Permissions.ForTool(ModuleId, "remove_clause"),
                Function = AIFunctionFactory.Create(clauses.RemoveClause, name: "remove_clause"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "add_playbook_rule",
                Permission = Permissions.ForTool(ModuleId, "add_playbook_rule"),
                Function = AIFunctionFactory.Create(clauses.AddPlaybookRule, name: "add_playbook_rule"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "remove_playbook_rule",
                Permission = Permissions.ForTool(ModuleId, "remove_playbook_rule"),
                Function = AIFunctionFactory.Create(clauses.RemovePlaybookRule, name: "remove_playbook_rule"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "start_bulk_review",
                Permission = Permissions.ForTool(ModuleId, "start_bulk_review"),
                Function = AIFunctionFactory.Create(matters.StartBulkReview, name: "start_bulk_review"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "index_matter_documents",
                Permission = Permissions.ForTool(ModuleId, "index_matter_documents"),
                Function = AIFunctionFactory.Create(matters.IndexMatterDocuments, name: "index_matter_documents"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "restrict_matter_access",
                Permission = Permissions.ForTool(ModuleId, "restrict_matter_access"),
                Function = AIFunctionFactory.Create(matters.RestrictMatterAccess, name: "restrict_matter_access"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "open_matter_access",
                Permission = Permissions.ForTool(ModuleId, "open_matter_access"),
                Function = AIFunctionFactory.Create(matters.OpenMatterAccess, name: "open_matter_access"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "connect_matter_folder",
                Permission = Permissions.ForTool(ModuleId, "connect_matter_folder"),
                Function = AIFunctionFactory.Create(matters.ConnectMatterFolder, name: "connect_matter_folder"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "sync_matter_folder",
                Permission = Permissions.ForTool(ModuleId, "sync_matter_folder"),
                Function = AIFunctionFactory.Create(matters.SyncMatterFolder, name: "sync_matter_folder"),
                RequiresApproval = true,
            },
        ];
    }
}
