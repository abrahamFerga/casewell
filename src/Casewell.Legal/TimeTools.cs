using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Application.Documents;
using Cortex.Application.Files;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Time capture and pre-billing — the "Log half an hour — call with opposing counsel" flow the
/// README promises. Logging is the module's one deliberately non-approval-gated write: capture
/// friction is why lawyers under-record time, and an entry is own-user, append-only, and
/// correctable with a follow-up entry. The pre-bill (a filed document) stays approval-gated
/// like every other record-changing action.
/// </summary>
public sealed class TimeTools(
    LegalDbContext db,
    IFileStore files,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IPdfRenderer pdfRenderer)
{
    [Description("Log time worked on a matter (billable by default). Quick capture: not approval-gated — entries are append-only and correctable with a follow-up entry.")]
    public async Task<string> LogTime(
        [Description("The matter name the time was spent on.")] string matterName,
        [Description("Hours worked, e.g. 0.5 or 2.")] double hours,
        [Description("What was done — the narrative line for the bill, e.g. 'Drafted NDA; call with client'.")] string description,
        [Description("The day the work happened as an ISO date (default: today).")] string? date = null,
        [Description("Whether the time is billable (default true).")] bool billable = true,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (!TimeCapture.HoursAreValid(hours))
        {
            return "hours must be greater than 0 and at most 24 per entry.";
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return "A time entry needs a description — it becomes the narrative line on the bill.";
        }

        var workedOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, CultureInfo.InvariantCulture, out workedOn))
        {
            return $"'{date}' is not a date I can parse — use an ISO date like 2026-07-07, or omit it for today.";
        }

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
            Hours = (decimal)hours,
            Description = description.Trim(),
            WorkedOn = workedOn,
            Billable = billable,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Logged {hours:0.##}h on matter '{matter.Name}' for {workedOn:yyyy-MM-dd}" +
               $"{(billable ? "" : " (non-billable)")}: {description.Trim()}";
    }

    [Description("List logged time: one matter's entries with totals, or (with no matter) the caller's own recent time across matters.")]
    public async Task<string> ListTime(
        [Description("Optional matter name; omit for your own recent time across all matters.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }

            var entries = await db.TimeEntries
                .Where(t => t.MatterId == matter.Id)
                .OrderByDescending(t => t.WorkedOn)
                .Take(100)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                return $"No time logged on matter '{matter.Name}' yet. Log some with log_time.";
            }

            var sb = new StringBuilder($"Time on matter '{matter.Name}':\n");
            foreach (var e in entries)
            {
                sb.AppendLine($"- {e.WorkedOn:yyyy-MM-dd} · {e.Hours:0.##}h · {e.Description}" +
                              $"{(e.Billable ? "" : " (non-billable)")}{(e.UserDisplay is null ? "" : $" — {e.UserDisplay}")}");
            }

            sb.Append($"Total: {entries.Sum(e => e.Hours):0.##}h ({entries.Where(e => e.Billable).Sum(e => e.Hours):0.##}h billable).");
            return sb.ToString();
        }

        // No matter: the caller's own last 14 days, grouped per matter — the "what did I do this
        // week" view. Walls are irrelevant here: these are the caller's own entries.
        var since = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(-14);
        var mine = await db.TimeEntries
            .Where(t => t.UserId == currentUser.UserId && t.WorkedOn >= since)
            .Join(db.Matters, t => t.MatterId, m => m.Id, (t, m) => new { t.Hours, t.Billable, t.WorkedOn, MatterName = m.Name })
            .OrderByDescending(t => t.WorkedOn)
            .Take(200)
            .ToListAsync(cancellationToken);
        if (mine.Count == 0)
        {
            return "You have no time logged in the last 14 days. Log some with log_time (matter, hours, what you did).";
        }

        var byMatter = mine.GroupBy(t => t.MatterName)
            .OrderByDescending(g => g.Sum(t => t.Hours));
        var summary = new StringBuilder($"Your time, last 14 days ({mine.Sum(t => t.Hours):0.##}h total):\n");
        foreach (var g in byMatter)
        {
            summary.AppendLine($"- {g.Key}: {g.Sum(t => t.Hours):0.##}h ({g.Where(t => t.Billable).Sum(t => t.Hours):0.##}h billable)");
        }

        return summary.ToString();
    }

    [Description("Generate a PRE-BILL for a matter: its time entries over an optional date range with billable totals, rendered as a PDF and filed on the matter for billing review.")]
    public async Task<string> ExportPrebill(
        [Description("The matter name.")] string matterName,
        [Description("Optional period start as an ISO date (inclusive); omit for all time.")] string? fromDate = null,
        [Description("Optional period end as an ISO date (inclusive); omit for today.")] string? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var from = DateOnly.MinValue;
        if (!string.IsNullOrWhiteSpace(fromDate) && !DateOnly.TryParse(fromDate, CultureInfo.InvariantCulture, out from))
        {
            return $"'{fromDate}' is not a date I can parse — use an ISO date like 2026-07-01, or omit it.";
        }

        var to = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(toDate) && !DateOnly.TryParse(toDate, CultureInfo.InvariantCulture, out to))
        {
            return $"'{toDate}' is not a date I can parse — use an ISO date like 2026-07-31, or omit it for today.";
        }

        var entries = await db.TimeEntries
            .Where(t => t.MatterId == matter.Id && t.WorkedOn >= from && t.WorkedOn <= to)
            .OrderBy(t => t.WorkedOn).ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return $"No time entries on matter '{matter.Name}' in that period — nothing to pre-bill.";
        }

        var period = TimeCapture.PeriodLabel(from, to);
        var body = TimeCapture.ComposePrebill(matter.Name, matter.ClientName, period, entries);
        var billable = entries.Where(e => e.Billable).Sum(e => e.Hours);

        // Render + store + file on the matter in one step, so the pre-bill can't end up orphaned.
        var pdf = pdfRenderer.Render($"Pre-bill — {matter.Name}", body);
        using var stream = new MemoryStream(pdf);
        var stored = await files.SaveAsync(
            $"prebill-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf", "application/pdf", stream,
            source: "prebill", cancellationToken);

        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            FileId = stored.Id,
            FileName = stored.FileName,
            Note = $"pre-bill {period}: {billable:0.##}h billable of {entries.Sum(e => e.Hours):0.##}h",
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Filed pre-bill '{stored.FileName}' (file id: {stored.Id}) on matter '{matter.Name}': " +
               $"{entries.Count} entr(ies), {billable:0.##}h billable, {entries.Where(e => !e.Billable).Sum(e => e.Hours):0.##}h non-billable ({period}).";
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}

/// <summary>Pure helpers behind the time tools, unit-tested without a database.</summary>
public static class TimeCapture
{
    /// <summary>An entry is positive and at most a day — beyond that it's a typo, not a workday.</summary>
    public static bool HoursAreValid(double hours) => hours is > 0 and <= 24;

    /// <summary>The Monday starting <paramref name="day"/>'s week — the x bucket of the Hours chart.</summary>
    public static DateOnly WeekOf(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>"inception – 2026-07-31" or "2026-07-01 – 2026-07-31".</summary>
    public static string PeriodLabel(DateOnly from, DateOnly to) =>
        $"{(from == DateOnly.MinValue ? "inception" : from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))} – {to:yyyy-MM-dd}";

    /// <summary>The pre-bill body: one narrative line per entry, then billable/non-billable totals.</summary>
    public static string ComposePrebill(
        string matterName, string? clientName, string period, IReadOnlyList<TimeEntry> entries)
    {
        var billable = entries.Where(e => e.Billable).Sum(e => e.Hours);
        var nonBillable = entries.Where(e => !e.Billable).Sum(e => e.Hours);

        var body = new StringBuilder();
        body.AppendLine($"Matter: {matterName}{(clientName is null ? "" : $"   Client: {clientName}")}");
        body.AppendLine($"Period: {period}   Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd}");
        body.AppendLine();
        foreach (var e in entries)
        {
            body.AppendLine($"{e.WorkedOn:yyyy-MM-dd}  {e.Hours,5:0.##}h  {e.Description}" +
                            $"{(e.Billable ? "" : "  [non-billable]")}{(e.UserDisplay is null ? "" : $"  — {e.UserDisplay}")}");
        }

        body.AppendLine();
        body.AppendLine($"Billable: {billable:0.##}h   Non-billable: {nonBillable:0.##}h   Total: {billable + nonBillable:0.##}h");
        body.AppendLine();
        body.AppendLine("Draft pre-bill for internal billing review — not an invoice.");
        return body.ToString();
    }
}
