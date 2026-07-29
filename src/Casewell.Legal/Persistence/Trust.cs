using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>Client funds move in (deposit) or out (disbursement); the sign lives here, never on the amount.</summary>
public enum TrustTransactionType
{
    Deposit,
    Disbursement,
}

/// <summary>
/// One movement of client funds on a matter's trust ledger (IOLTA). Compliance record, not
/// bookkeeping convenience: append-only (corrections are contra entries, never edits), every
/// write approval-gated, and a disbursement that would overdraw the matter's balance is refused
/// outright — a negative client ledger is treated as misappropriation even when the account
/// total is positive (Model Rule 1.15). Bank feeds are out of scope: this ledger records what
/// happened at the bank; it does not move money.
/// </summary>
public sealed class TrustTransaction : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public TrustTransactionType Type { get; set; }

    /// <summary>Always positive; direction comes from <see cref="Type"/>.</summary>
    public decimal Amount { get; set; }

    /// <summary>What the money was for — "retainer received", "filing fee paid", "settlement to client".</summary>
    public required string Description { get; set; }

    /// <summary>Optional bank-side reference: check number, wire confirmation, receipt id.</summary>
    public string? Reference { get; set; }

    /// <summary>The banking date (defaults to today at record time).</summary>
    public DateOnly OccurredOn { get; set; }

    /// <summary>Who recorded the entry (the acting user).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Display-name snapshot at record time, so the ledger reads without a user join.</summary>
    public string? UserDisplay { get; set; }
}
