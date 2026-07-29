using System.Security.Cryptography;
using System.Text;
using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A party on a matter (client, opposing party, related entity) — the surface conflict checks
/// search. Names are display-level; the PM system stays the system of record for contacts.
/// </summary>
public sealed class MatterParty : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public required string Name { get; set; }

    /// <summary>client | opposing | related — free but suggested vocabulary.</summary>
    public required string Role { get; set; }
}

/// <summary>
/// One tamper-evident conflict-check record (ADR-0006). Rows form an append-only hash chain per
/// matter: each hash covers the data snapshot, the previous attestation's hash, the timestamp,
/// and the attesting user — so any later edit to any row breaks every hash after it. The chain
/// proves WHAT was searched and WHEN, not that the search was exhaustive.
/// </summary>
public sealed class ConflictAttestation : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public Guid AttestedByUserId { get; set; }

    public DateTimeOffset PerformedAt { get; set; }

    /// <summary>The names that were searched, as a JSON array.</summary>
    public required string SearchTermsJson { get; set; }

    /// <summary>What the search found at that moment (matches, or an explicit empty result).</summary>
    public required string DataSnapshotJson { get; set; }

    /// <summary>Hash of the matter's previous attestation; null for the first link.</summary>
    public string? PriorAttestationHash { get; set; }

    public required string AttestationHash { get; set; }
}

/// <summary>The ADR-0006 hash chain: computation and verification, kept pure for testability.</summary>
public static class ConflictChain
{
    public static string ComputeHash(
        string dataSnapshotJson, string? priorAttestationHash, DateTimeOffset performedAt, Guid attestedByUserId)
    {
        // Round-trip ("O") format pins the timestamp representation; the hash must be recomputable
        // from the stored row alone, forever.
        var material = $"{dataSnapshotJson}\n{priorAttestationHash}\n{performedAt:O}\n{attestedByUserId:D}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// Recomputes every link of a matter's chain (oldest first). Returns the index of the first
    /// broken link, or null when the chain is intact.
    /// </summary>
    public static int? FindBrokenLink(IReadOnlyList<ConflictAttestation> chainOldestFirst)
    {
        string? priorHash = null;
        for (var i = 0; i < chainOldestFirst.Count; i++)
        {
            var link = chainOldestFirst[i];
            if (link.PriorAttestationHash != priorHash
                || link.AttestationHash != ComputeHash(link.DataSnapshotJson, priorHash, link.PerformedAt, link.AttestedByUserId))
            {
                return i;
            }

            priorHash = link.AttestationHash;
        }

        return null;
    }
}
