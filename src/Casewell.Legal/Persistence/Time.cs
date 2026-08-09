using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A billable (or non-billable) time entry on a matter — quick-capture from chat ("log 0.5h on
/// Acme for the NDA call"). Deliberately append-only and low-ceremony: capture friction is why
/// lawyers under-record time, so logging is NOT approval-gated (the module's one deliberate
/// exception); entries are own-user, low-stakes, and correctable by a follow-up entry.
/// </summary>
public sealed class TimeEntry : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>Who did the work (the logging user).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Display-name snapshot at log time, so listings read without a user join.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Hours worked (decimal, e.g. 0.5). Bounded to a day per entry.</summary>
    public decimal Hours { get; set; }

    /// <summary>What was done — becomes the narrative line on the bill.</summary>
    public required string Description { get; set; }

    /// <summary>The day the work happened (defaults to today at log time).</summary>
    public DateOnly WorkedOn { get; set; }

    public bool Billable { get; set; } = true;

    /// <summary>
    /// UTBMS-style activity code (L110, A103, …). Optional on entries logged before billing
    /// existed; <c>start_timer</c> and <c>log_time</c> suggest one from the description when the
    /// caller omits it, because an uncoded entry is the one a client challenges.
    /// </summary>
    public string? ActivityCode { get; set; }

    /// <summary>
    /// Rate agreed for this entry, when it differs from the rate used at invoice time. Null means
    /// "bill at whatever rate the invoice is drawn on".
    /// </summary>
    public decimal? Rate { get; set; }

    /// <summary>Set when an invoice draft picks this entry up; null means unbilled.</summary>
    public Guid? InvoiceId { get; set; }
}
