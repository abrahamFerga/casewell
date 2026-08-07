using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A timer the user left running. One row per user per tenant (enforced by a unique index): a
/// second start is a mistake, not a second stopwatch. Deliberately separate from
/// <see cref="TimeEntry"/> — nothing is billable until the timer stops and an entry is written.
/// </summary>
public sealed class RunningTimer : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>Whose timer. The unique index is on (tenant, user).</summary>
    public Guid? UserId { get; set; }

    public string? UserDisplay { get; set; }

    /// <summary>UTBMS-style activity code, suggested from the description when not supplied.</summary>
    public string? ActivityCode { get; set; }

    /// <summary>What is being worked on; becomes the entry's narrative when the timer stops.</summary>
    public string? Description { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>
/// A disbursement the firm advanced on a matter and expects to recover — filing fees, couriers,
/// expert invoices, transcripts. Approval-gated because it lands on a client's bill, and linked to
/// the invoice that billed it so it can never be billed twice.
/// </summary>
public sealed class Expense : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public Guid? UserId { get; set; }

    public string? UserDisplay { get; set; }

    /// <summary>Always positive.</summary>
    public decimal Amount { get; set; }

    public required string Description { get; set; }

    public DateOnly IncurredOn { get; set; }

    public bool Billable { get; set; } = true;

    /// <summary>Set when an invoice draft picks this up; null means unbilled.</summary>
    public Guid? InvoiceId { get; set; }
}

/// <summary>Draft → approved → sent. Void is the exit for a draft that should never have existed.</summary>
public enum InvoiceStatus
{
    Draft,
    Approved,
    Sent,
    Void,
}

/// <summary>What a line was built from — the three sources the issue's one-click draft combines.</summary>
public enum InvoiceLineKind
{
    Time,
    Expense,
    FlatFee,
}

/// <summary>
/// A client invoice assembled from unbilled time, unbilled expenses, and an optional flat fee.
/// The separation-of-duties rule lives on this record: <see cref="PreparedByUserId"/> is captured
/// at draft time so approval can refuse the preparer. An attorney approving their own invoice is
/// the control failure this whole workflow exists to prevent.
/// </summary>
public sealed class Invoice : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>Human-facing number, unique per tenant — "INV-2026-0001".</summary>
    public required string Number { get; set; }

    public InvoiceStatus Status { get; set; }

    /// <summary>Null means "since inception".</summary>
    public DateOnly? PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    /// <summary>Whose work product the draft is — the user who may NOT approve it.</summary>
    public Guid? PreparedByUserId { get; set; }

    public string? PreparedByDisplay { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public string? ApprovedByDisplay { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Sum of the lines, stored so a historical invoice never re-derives from moved data.</summary>
    public decimal Total { get; set; }

    public List<InvoiceLine> Lines { get; } = [];
}

/// <summary>One billed line. Time lines carry hours × rate; expenses and flat fees carry an amount.</summary>
public sealed class InvoiceLine : TenantEntityBase
{
    public Guid InvoiceId { get; set; }

    public InvoiceLineKind Kind { get; set; }

    public required string Description { get; set; }

    /// <summary>Hours, for time lines. Null on expense and flat-fee lines.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Hourly rate, for time lines.</summary>
    public decimal? Rate { get; set; }

    public decimal Amount { get; set; }

    /// <summary>The TimeEntry or Expense this came from, for audit back to the source row.</summary>
    public Guid? SourceId { get; set; }

    public DateOnly OccurredOn { get; set; }
}
