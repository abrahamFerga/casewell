using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Matter to-dos — the working list between conversations ("what's open on Meridian?"). Adding
/// and completing are record changes and approval-gated like the rest of the module; listing
/// honors ethical walls (a walled matter's tasks are invisible from outside the wall).
/// </summary>
public sealed class TaskTools(
    LegalDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Add a task (to-do) on a matter, optionally assigned to someone by name with a target date. For hard dates with reminder obligations use add_matter_event instead.")]
    public async Task<string> AddTask(
        [Description("The matter name.")] string matterName,
        [Description("What needs doing, e.g. 'Draft the motion to dismiss'.")] string title,
        [Description("Optional assignee by name, e.g. 'Maria' or 'paralegal team'.")] string? assignedTo = null,
        [Description("Optional target date as an ISO date, e.g. 2026-08-01.")] string? dueDate = null,
        [Description("Optional notes.")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return "A task needs a title — what needs doing?";
        }

        DateOnly? dueOn = null;
        if (!string.IsNullOrWhiteSpace(dueDate))
        {
            if (!DateOnly.TryParse(dueDate, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{dueDate}' is not a date I can parse — use an ISO date like 2026-08-01, or omit it.";
            }

            dueOn = parsed;
        }

        db.MatterTasks.Add(new MatterTask
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Title = title.Trim(),
            AssignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim(),
            DueOn = dueOn,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedByUserId = currentUser.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Added task '{title.Trim()}' on matter '{matter.Name}'" +
               $"{(assignedTo is null ? "" : $", assigned to {assignedTo.Trim()}")}" +
               $"{(dueOn is null ? "" : $", target {dueOn:yyyy-MM-dd}")}.";
    }

    [Description("List open tasks — across all matters, or for one matter. Completed tasks are excluded; dated tasks come first.")]
    public async Task<string> ListTasks(
        [Description("Optional matter name to filter to; omit for all matters.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        Matter? matter = null;
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }
        }

        var query = db.MatterTasks.Where(t => t.CompletedAt == null);
        if (matter is not null)
        {
            query = query.Where(t => t.MatterId == matter.Id);
        }

        var tasks = await query
            .OrderBy(t => t.DueOn == null) // dated tasks first, soonest first
            .ThenBy(t => t.DueOn)
            .ThenBy(t => t.CreatedAt)
            .Take(100)
            .Join(db.Matters.Where(m => m.Status == MatterStatus.Open), t => t.MatterId, m => m.Id,
                (t, m) => new { t.Title, t.AssignedTo, t.DueOn, t.Notes, MatterName = m.Name, m.RestrictedUserIdsJson })
            .ToListAsync(cancellationToken);

        var visible = tasks.Where(t => Matter.WallAllows(t.RestrictedUserIdsJson, currentUser.UserId)).ToList();
        if (visible.Count == 0)
        {
            return matter is null
                ? "No open tasks. Add one with add_task."
                : $"Matter '{matter.Name}' has no open tasks. Add one with add_task.";
        }

        var sb = new StringBuilder("Open tasks:\n");
        foreach (var t in visible)
        {
            sb.AppendLine($"- {t.Title} — matter '{t.MatterName}'" +
                          $"{(t.AssignedTo is null ? "" : $", assigned to {t.AssignedTo}")}" +
                          $"{(t.DueOn is null ? "" : $", target {t.DueOn:yyyy-MM-dd}")}" +
                          $"{(t.Notes is null ? "" : $" — {t.Notes}")}");
        }

        return sb.ToString();
    }

    [Description("Mark a task on a matter as completed so it leaves the open list.")]
    public async Task<string> CompleteTask(
        [Description("The matter name the task is on.")] string matterName,
        [Description("The task title (as shown by list_tasks).")] string title,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var normalized = title.Trim();
        var task = await db.MatterTasks.FirstOrDefaultAsync(
            t => t.MatterId == matter.Id && t.CompletedAt == null && EF.Functions.ILike(t.Title, normalized),
            cancellationToken);
        if (task is null)
        {
            return $"No open task titled '{normalized}' on matter '{matter.Name}'. Check list_tasks for the exact title.";
        }

        task.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return $"Marked task '{task.Title}' on matter '{matter.Name}' as completed.";
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}
