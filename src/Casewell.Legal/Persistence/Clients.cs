using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A client of the firm — the contact-book side of the practice. Matters keep referencing clients
/// by display name (<see cref="Matter.ClientName"/>): the PM system stays the system of record for
/// engagements, and a client row is deliberately deletable without touching matter history. Unique
/// by name per tenant; conflict checks search this book alongside matter parties.
/// </summary>
public sealed class Client : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>Where client communications go; matters default their <c>ClientEmail</c> from it at creation.</summary>
    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>The company behind the contact, when the client is an organization.</summary>
    public string? Organization { get; set; }

    public string? Notes { get; set; }
}
