using System.Text.Json;
using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

public enum MatterStatus
{
    Open = 0,
    Closed = 1,
    OnHold = 2,
}

/// <summary>
/// A legal matter — the engagement-scoped workspace every legal-AI product organizes around
/// (Harvey's Vault, Legora's workspaces). Documents, drafts, and agent work product attach to a
/// matter; tenant isolation applies via the module's query filters.
/// </summary>
public sealed class Matter : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>
    /// The firm-facing docket number, assigned at creation as <c>YYYY-NNNN</c> (year of opening +
    /// per-tenant sequence). Null only on matters created before numbering existed.
    /// </summary>
    public string? MatterNumber { get; set; }

    /// <summary>The client this matter is for (display-level; the PM system stays the system of record).</summary>
    public string? ClientName { get; set; }

    /// <summary>Where reviewed client communications (status letters) are sent. Optional —
    /// without it, send_status_update refuses and the letter stays a filed draft.</summary>
    public string? ClientEmail { get; set; }

    /// <summary>Canonical practice area from <see cref="PracticeAreas"/>, or null when uncategorized.</summary>
    public string? PracticeArea { get; set; }

    public MatterStatus Status { get; set; } = MatterStatus.Open;

    /// <summary>Set when the matter closes; cleared on reopen.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Applies a status transition, keeping <see cref="ClosedAt"/> consistent. Any state may move
    /// to any other (firms reopen closed matters, hold open ones); the value of modeling it here is
    /// the timestamp bookkeeping and a single audited mutation path, not transition denial.
    /// </summary>
    public string ApplyStatus(MatterStatus next, DateTimeOffset now)
    {
        var previous = Status;
        Status = next;
        ClosedAt = next == MatterStatus.Closed ? (ClosedAt ?? now) : null;
        return previous == next
            ? $"Matter '{Name}' was already {Describe(next)}."
            : $"Matter '{Name}' is now {Describe(next)} (was {Describe(previous)}).";
    }

    private static string Describe(MatterStatus status) => status switch
    {
        MatterStatus.OnHold => "on hold",
        MatterStatus.Closed => "closed",
        _ => "open",
    };

    /// <summary>
    /// The ethical wall (legal v1 item 10): null means open to the whole tenant; a JSON array of
    /// user ids means ONLY those users can see or use the matter — everywhere (tools, tabs, and
    /// the matter's RAG collection). Walls restrict by identity, not permission, so even a
    /// wildcard-permission user outside the wall is excluded.
    /// </summary>
    public string? RestrictedUserIdsJson { get; set; }

    public ICollection<MatterDocument> Documents { get; set; } = [];

    /// <summary>Whether <paramref name="userId"/> is inside this matter's wall (or there is none).</summary>
    public bool IsAccessibleTo(Guid? userId) => WallAllows(RestrictedUserIdsJson, userId);

    /// <summary>The wall check itself, usable on projections that carry only the JSON column.</summary>
    public static bool WallAllows(string? restrictedUserIdsJson, Guid? userId)
    {
        if (restrictedUserIdsJson is null)
        {
            return true;
        }

        if (userId is null)
        {
            return false; // fail closed: no identity, no walled access
        }

        try
        {
            var allowed = JsonSerializer.Deserialize<Guid[]>(restrictedUserIdsJson) ?? [];
            return allowed.Contains(userId.Value);
        }
        catch (JsonException)
        {
            return false; // fail closed on a corrupt wall rather than opening the matter
        }
    }
}

/// <summary>
/// A platform file (<c>StoredFile</c>) attached to a matter. The platform file store keeps the bytes
/// and metadata; this row is the matter-scoped association plus a display-name snapshot.
/// </summary>
public sealed class MatterDocument : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>The platform <c>StoredFile</c> id — what the document tools (read_document) consume.</summary>
    public Guid FileId { get; set; }

    public required string FileName { get; set; }

    /// <summary>Optional note recorded at attach time (e.g. "signed original", "client draft").</summary>
    public string? Note { get; set; }
}
