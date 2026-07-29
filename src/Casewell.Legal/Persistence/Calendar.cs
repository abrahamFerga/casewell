using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A dated event on a matter: a court deadline, hearing, meeting, or plain reminder. Deadlines are
/// the malpractice surface of legal practice — the module keeps them queryable per matter and as a
/// tenant-wide agenda with due-soon/overdue surfacing. Delivery to external channels (email,
/// Outlook two-way sync via the msgraph connector) is a later feature; the agenda IS the v1
/// reminder mechanism.
/// </summary>
public sealed class MatterEvent : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public required string Title { get; set; }

    /// <summary>deadline | hearing | meeting | reminder.</summary>
    public required string Type { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public string? Notes { get; set; }

    /// <summary>Set when the obligation is satisfied — completed events leave the agenda and
    /// stop counting against matter close-out.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Who docketed the event — the reminder recipient. Null on pre-reminder-era rows,
    /// which therefore never notify.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>Reminder progress: 0 = none sent, 1 = lead-time nudge sent, 2 = overdue notice
    /// sent. Monotonic — each stage fires at most once per event.</summary>
    public int ReminderStage { get; set; }

    /// <summary>How the event reads on an agenda relative to <paramref name="now"/>.</summary>
    public static EventUrgency UrgencyAt(DateTimeOffset now, DateTimeOffset startsAt, int dueSoonDays = 3) =>
        startsAt < now ? EventUrgency.Overdue
        : startsAt <= now.AddDays(dueSoonDays) ? EventUrgency.DueSoon
        : EventUrgency.Upcoming;

    /// <summary>The next reminder stage due, or null when nothing should fire: stage 1 inside
    /// the lead window, stage 2 once overdue — each sent at most once, in order or skipped past
    /// (an event discovered already overdue gets only the overdue notice).</summary>
    public static int? ReminderStageDue(DateTimeOffset now, DateTimeOffset startsAt, int currentStage, int leadDays) =>
        startsAt < now && currentStage < 2 ? 2
        : startsAt <= now.AddDays(leadDays) && currentStage < 1 ? 1
        : null;

    public static string NormalizeType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "deadline" or "due" or "filing" => "deadline",
        "hearing" or "court" or "trial" => "hearing",
        "meeting" or "call" or "appointment" => "meeting",
        _ => "reminder",
    };
}

public enum EventUrgency
{
    Upcoming = 0,
    DueSoon = 1,
    Overdue = 2,
}
