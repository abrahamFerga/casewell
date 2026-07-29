using System.ComponentModel;
using System.Text;
using Cortex.Application.Documents;
using Cortex.Application.Notifications;
using Cortex.Application.Files;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// The reporting pair: the one-look matter brief for the person working the matter ("brief me on
/// Meridian"), and the client-facing status letter — deliberately different audiences. The brief
/// shows everything (assignees, walls, urgency); the letter shows progress, dates, and effort and
/// NOTHING internal, and is filed as a DRAFT for attorney review before it ever reaches a client.
/// </summary>
public sealed class BriefingTools(
    LegalDbContext db,
    IFileStore files,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IPdfRenderer pdfRenderer,
    ISmtpTransport smtp)
{
    [Description("The one-look brief on a matter: status, parties, open events (overdue flagged), open tasks, time totals, and recent documents. Use to answer 'brief me on X' or before working a matter.")]
    public async Task<string> GetMatterOverview(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var now = DateTimeOffset.UtcNow;
        var parties = await db.MatterParties.Where(p => p.MatterId == matter.Id)
            .OrderBy(p => p.Role).ThenBy(p => p.Name).Take(20).ToListAsync(cancellationToken);
        var events = await db.MatterEvents.Where(e => e.MatterId == matter.Id && e.CompletedAt == null)
            .OrderBy(e => e.StartsAt).Take(10).ToListAsync(cancellationToken);
        var tasks = await db.MatterTasks.Where(t => t.MatterId == matter.Id && t.CompletedAt == null)
            .OrderBy(t => t.DueOn == null).ThenBy(t => t.DueOn).Take(10).ToListAsync(cancellationToken);
        var time = await db.TimeEntries.Where(t => t.MatterId == matter.Id).ToListAsync(cancellationToken);
        var documents = await db.MatterDocuments.Where(d => d.MatterId == matter.Id)
            .OrderByDescending(d => d.CreatedAt).Take(5).ToListAsync(cancellationToken);
        var documentCount = await db.MatterDocuments.CountAsync(d => d.MatterId == matter.Id, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"MATTER BRIEF: {matter.Name}{(matter.MatterNumber is null ? "" : $" [{matter.MatterNumber}]")}");
        sb.AppendLine($"Status: {matter.Status}{(matter.ClientName is null ? "" : $" · Client: {matter.ClientName}")}" +
                      $"{(matter.PracticeArea is null ? "" : $" · {matter.PracticeArea}")}" +
                      $"{(matter.RestrictedUserIdsJson is null ? "" : " · RESTRICTED (ethical wall)")}");

        sb.AppendLine(parties.Count == 0
            ? "Parties: none recorded — record them with add_matter_party (conflict checks depend on it)."
            : "Parties: " + string.Join("; ", parties.Select(p => $"{p.Name} ({p.Role.ToUpperInvariant()})")));

        if (events.Count == 0)
        {
            sb.AppendLine("Calendar: nothing open.");
        }
        else
        {
            sb.AppendLine("Open events:");
            foreach (var e in events)
            {
                var days = (int)Math.Ceiling((e.StartsAt - now).TotalDays);
                var when = days < 0 ? $"OVERDUE by {-days} day(s)" : days == 0 ? "due TODAY" : $"in {days} day(s)";
                sb.AppendLine($"  - {e.StartsAt:yyyy-MM-dd} · [{e.Type}] {e.Title} ({when})");
            }
        }

        if (tasks.Count == 0)
        {
            sb.AppendLine("Tasks: none open.");
        }
        else
        {
            sb.AppendLine("Open tasks:");
            foreach (var t in tasks)
            {
                sb.AppendLine($"  - {t.Title}{(t.AssignedTo is null ? "" : $" (assigned to {t.AssignedTo})")}" +
                              $"{(t.DueOn is null ? "" : $", target {t.DueOn:yyyy-MM-dd}")}");
            }
        }

        sb.AppendLine(time.Count == 0
            ? "Time: none logged."
            : $"Time: {time.Sum(t => t.Hours):0.##}h total, {time.Where(t => t.Billable).Sum(t => t.Hours):0.##}h billable across {time.Count} entr(ies).");

        sb.AppendLine(documentCount == 0
            ? "Documents: none attached."
            : $"Documents ({documentCount}): " + string.Join("; ", documents.Select(d => d.FileName)) +
              (documentCount > documents.Count ? " …" : ""));

        return sb.ToString();
    }

    [Description("Draft a CLIENT-FACING status update letter for a matter (recent progress, upcoming dates, hours worked — no internal notes or strategy) and file it on the matter as a PDF for attorney review before sending.")]
    public async Task<string> DraftStatusUpdate(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var body = await ComposeStatusLetterAsync(matter, cancellationToken);
        var pdf = pdfRenderer.Render($"Status update — {matter.Name}", body);
        using var stream = new MemoryStream(pdf);
        var stored = await files.SaveAsync(
            $"status-update-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf", "application/pdf", stream,
            source: "status_update", cancellationToken);

        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            FileId = stored.Id,
            FileName = stored.FileName,
            Note = "client status update (draft for attorney review)",
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Filed draft status letter '{stored.FileName}' (file id: {stored.Id}) on matter '{matter.Name}' " +
               "for attorney review before sending.";
    }

    [Description("EMAIL the current status letter to the matter's client — ONLY after an attorney has reviewed a drafted letter (draft_status_update). Recomposes from current data, sends to the matter's client email, and files the sent copy. Side-effecting, outward-facing, and requires human approval.")]
    public async Task<string> SendStatusUpdate(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (string.IsNullOrWhiteSpace(matter.ClientEmail))
        {
            return $"Matter '{matter.Name}' has no client email on file — the letter stays a draft. " +
                   "An attorney can add one when creating the matter (create_matter's clientEmail).";
        }

        if (!smtp.IsConfigured)
        {
            return "No email transport is configured (Email: section), so the letter cannot be sent. " +
                   "It can still be drafted and delivered manually (draft_status_update).";
        }

        var letter = await ComposeStatusLetterAsync(matter, cancellationToken);
        // The client copy carries no DRAFT banner — the approval that released this send IS the review record.
        var clientBody = letter.Replace(DraftBanner, "").TrimEnd() + "\n";
        await smtp.SendAsync(new EmailMessage(
            matter.ClientEmail!, $"Status update — {matter.Name}", clientBody), cancellationToken);

        var pdf = pdfRenderer.Render($"Status update — {matter.Name} (sent)", clientBody);
        using var sentStream = new MemoryStream(pdf);
        var sentCopy = await files.SaveAsync(
            $"status-update-sent-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf", "application/pdf", sentStream,
            source: "status_update", cancellationToken);
        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            FileId = sentCopy.Id,
            FileName = sentCopy.FileName,
            Note = $"status letter SENT to {matter.ClientEmail} on {DateTimeOffset.UtcNow:yyyy-MM-dd}",
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Sent the status letter for matter '{matter.Name}' to {matter.ClientEmail} and filed the sent copy " +
               $"('{sentCopy.FileName}', file id: {sentCopy.Id}).";
    }

    private const string DraftBanner = "DRAFT — for attorney review before sending to the client.";

    private async Task<string> ComposeStatusLetterAsync(Matter matter, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-30);
        var recentEvents = await db.MatterEvents
            .Where(e => e.MatterId == matter.Id && e.CompletedAt >= since)
            .OrderByDescending(e => e.CompletedAt).Take(10).ToListAsync(cancellationToken);
        var recentTasks = await db.MatterTasks
            .Where(t => t.MatterId == matter.Id && t.CompletedAt >= since)
            .OrderByDescending(t => t.CompletedAt).Take(10).ToListAsync(cancellationToken);
        var upcoming = await db.MatterEvents
            .Where(e => e.MatterId == matter.Id && e.CompletedAt == null && e.StartsAt >= now && e.StartsAt <= now.AddDays(60))
            .OrderBy(e => e.StartsAt).Take(10).ToListAsync(cancellationToken);
        var sinceDay = DateOnly.FromDateTime(since.UtcDateTime);
        var recentHours = await db.TimeEntries
            .Where(t => t.MatterId == matter.Id && t.WorkedOn >= sinceDay)
            .SumAsync(t => (decimal?)t.Hours, cancellationToken) ?? 0m;

        // Client-facing on purpose: progress, dates, and effort — no internal notes, assignees,
        // billing rates, or strategy. The letter is a DRAFT the attorney reviews before sending.
        var body = new StringBuilder();
        body.AppendLine($"Re: {matter.Name}");
        body.AppendLine($"Date: {now:yyyy-MM-dd}");
        body.AppendLine();
        body.AppendLine($"Dear {matter.ClientName ?? "Client"},");
        body.AppendLine();
        body.AppendLine("Here is the current status of your matter.");
        body.AppendLine();

        if (recentEvents.Count > 0 || recentTasks.Count > 0)
        {
            body.AppendLine("Progress in the last 30 days:");
            foreach (var e in recentEvents)
            {
                body.AppendLine($"  - Completed: {e.Title} ({e.CompletedAt:yyyy-MM-dd})");
            }

            foreach (var t in recentTasks)
            {
                body.AppendLine($"  - Completed: {t.Title} ({t.CompletedAt:yyyy-MM-dd})");
            }
        }
        else
        {
            body.AppendLine("Progress in the last 30 days: work is ongoing; no milestones completed in this period.");
        }

        body.AppendLine();
        if (upcoming.Count > 0)
        {
            body.AppendLine("Upcoming dates:");
            foreach (var e in upcoming)
            {
                body.AppendLine($"  - {e.StartsAt:yyyy-MM-dd}: {e.Title}");
            }
        }
        else
        {
            body.AppendLine("Upcoming dates: none scheduled in the next 60 days.");
        }

        body.AppendLine();
        body.AppendLine($"Time devoted to your matter in the last 30 days: {recentHours:0.##} hours.");
        body.AppendLine();
        body.AppendLine("Please contact us with any questions.");
        body.AppendLine();
        body.AppendLine("Sincerely,");
        body.AppendLine("[Attorney name]");
        body.AppendLine();
        body.AppendLine(DraftBanner);

        return body.ToString();
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}
