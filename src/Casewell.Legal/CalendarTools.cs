using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Matter calendar: deadlines, hearings, meetings, reminders. The agenda (list_upcoming_events)
/// is the v1 reminder mechanism — OVERDUE and DUE SOON markers surface what needs attention;
/// walled matters' events are visible only inside the wall, like everything else matter-scoped.
/// </summary>
public sealed class CalendarTools(
    LegalDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Add a dated event to a matter: a deadline, hearing, meeting, or reminder. Side-effecting and requires approval.")]
    public async Task<string> AddMatterEvent(
        [Description("The matter name.")] string matterName,
        [Description("What the event is, e.g. 'Answer due' or 'Summary judgment hearing'.")] string title,
        [Description("When, as ISO 8601 (e.g. '2026-08-14' or '2026-08-14T15:00:00Z').")] string when,
        [Description("The event type: deadline, hearing, meeting, or reminder.")] string type = "reminder",
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
            return "An event needs a title.";
        }

        if (!DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var startsAt))
        {
            return $"'{when}' is not a date I can parse. Use ISO 8601, e.g. '2026-08-14' or '2026-08-14T15:00:00Z'.";
        }

        var evt = new MatterEvent
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Title = title.Trim(),
            Type = MatterEvent.NormalizeType(type),
            CreatedByUserId = currentUser.UserId,
            StartsAt = startsAt,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
        db.MatterEvents.Add(evt);
        await db.SaveChangesAsync(cancellationToken);

        return $"Added {evt.Type} '{evt.Title}' on matter '{matter.Name}' for {evt.StartsAt:yyyy-MM-dd HH:mm}Z.";
    }

    [Description("List a matter's calendar events (soonest first) with overdue / due-soon markers.")]
    public async Task<string> ListMatterEvents(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var events = await db.MatterEvents
            .Where(e => e.MatterId == matter.Id)
            .OrderBy(e => e.StartsAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (events.Count == 0)
        {
            return $"Matter '{matter.Name}' has no calendar events. Add one with add_matter_event.";
        }

        var sb = new StringBuilder($"Calendar for matter '{matter.Name}':\n");
        AppendAgendaLines(sb, events.Select(e => (e, matter.Name)));
        return sb.ToString();
    }

    [Description("The firm agenda: upcoming events across all matters you can access (soonest first), with overdue and due-soon markers. This is the reminder surface — check it at the start of a working session.")]
    public async Task<string> ListUpcomingEvents(
        [Description("How many days ahead to look (default 14). Overdue events always show.")] int daysAhead = 14,
        CancellationToken cancellationToken = default)
    {
        var horizon = DateTimeOffset.UtcNow.AddDays(Math.Clamp(daysAhead, 1, 365));

        // Wall filtering happens against the matter list in memory, same as everywhere else.
        var matters = (await db.Matters
                .Where(m => m.Status != MatterStatus.Closed) // closed matters stop reminding
                .Select(m => new { m.Id, m.Name, m.RestrictedUserIdsJson })
                .ToListAsync(cancellationToken))
            .Where(m => Matter.WallAllows(m.RestrictedUserIdsJson, currentUser.UserId))
            .ToDictionary(m => m.Id, m => m.Name);

        var events = (await db.MatterEvents
                .Where(e => e.CompletedAt == null && e.StartsAt <= horizon)
                .OrderBy(e => e.StartsAt)
                .Take(300)
                .ToListAsync(cancellationToken))
            .Where(e => matters.ContainsKey(e.MatterId))
            .Take(50)
            .ToList();
        if (events.Count == 0)
        {
            return $"Nothing on the agenda in the next {daysAhead} day(s). Add events with add_matter_event.";
        }

        var sb = new StringBuilder($"Agenda (next {daysAhead} day(s), plus anything overdue):\n");
        AppendAgendaLines(sb, events.Select(e => (e, matters[e.MatterId])));
        return sb.ToString();
    }

    private static void AppendAgendaLines(StringBuilder sb, IEnumerable<(MatterEvent Event, string MatterName)> rows)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (e, matterName) in rows)
        {
            var marker = e.CompletedAt is not null ? " ✓ done" : MatterEvent.UrgencyAt(now, e.StartsAt) switch
            {
                EventUrgency.Overdue => " ⚠ OVERDUE",
                EventUrgency.DueSoon => " ⏰ DUE SOON",
                _ => "",
            };
            sb.AppendLine(
                $"- {e.StartsAt:yyyy-MM-dd HH:mm}Z [{e.Type}] {e.Title} — {matterName}{marker}" +
                $"{(e.Notes is null ? "" : $" ({e.Notes})")}");
        }
    }

    [Description("Mark a matter's calendar event as completed (the obligation is satisfied): it leaves the agenda, stops reminding, and stops blocking matter close-out. Side-effecting and requires approval.")]
    public async Task<string> CompleteEvent(
        [Description("The matter name the event is on.")] string matterName,
        [Description("The event title (as shown by list_matter_events).")] string title,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var normalized = title.Trim();
        var evt = await db.MatterEvents.FirstOrDefaultAsync(
            e => e.MatterId == matter.Id && e.CompletedAt == null && EF.Functions.ILike(e.Title, normalized),
            cancellationToken);
        if (evt is null)
        {
            return $"No open event titled '{normalized}' on matter '{matter.Name}'. Check list_matter_events for the exact title.";
        }

        evt.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return $"Marked event '{evt.Title}' ({evt.StartsAt:yyyy-MM-dd}) on matter '{matter.Name}' as completed.";
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}
