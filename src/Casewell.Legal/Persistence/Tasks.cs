using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A to-do on a matter ("draft the motion to dismiss") — softer than a docketed calendar event:
/// a free-text assignee (real shops assign to a person by name, not an account id) and an
/// optional target date with no reminder machinery. Hard dates with reminder obligations belong
/// in <see cref="MatterEvent"/> instead.
/// </summary>
public sealed class MatterTask : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public required string Title { get; set; }

    public string? Notes { get; set; }

    /// <summary>Who it's assigned to, as displayed (e.g. "Maria", "paralegal team").</summary>
    public string? AssignedTo { get; set; }

    /// <summary>Optional target date — softer than a docketed deadline; no reminder machinery.</summary>
    public DateOnly? DueOn { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Who created the task (the chat caller).</summary>
    public Guid? CreatedByUserId { get; set; }
}
