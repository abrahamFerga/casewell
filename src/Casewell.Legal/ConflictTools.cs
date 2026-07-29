using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// The tamper-evident conflict-check workflow (ADR-0006). check_conflicts searches every matter's
/// parties and client names — INCLUDING walled matters, because a wall that hid conflicts would
/// defeat the check's purpose — but masks matters the caller can't access ("a restricted matter"),
/// so a hit is disclosed without leaking the matter itself. attest_conflict_check then freezes what
/// was searched and found into the matter's append-only hash chain.
/// </summary>
public sealed class ConflictTools(
    LegalDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Record a party on a matter (the client, an opposing party, or a related entity). Parties are what conflict checks search. Side-effecting and requires approval.")]
    public async Task<string> AddMatterParty(
        [Description("The matter name.")] string matterName,
        [Description("The party's name, e.g. 'Initech LLC' or 'Jane Roe'.")] string partyName,
        [Description("The party's role: client, opposing, or related.")] string role = "related",
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var name = partyName.Trim();
        if (name.Length == 0)
        {
            return "A party needs a name.";
        }

        var normalizedRole = role.Trim().ToLowerInvariant() switch
        {
            "client" => "client",
            "opposing" or "adverse" or "opponent" => "opposing",
            _ => "related",
        };

        var exists = await db.MatterParties.AnyAsync(
            p => p.MatterId == matter.Id && EF.Functions.ILike(p.Name, name), cancellationToken);
        if (exists)
        {
            return $"'{name}' is already a party on matter '{matter.Name}'.";
        }

        db.MatterParties.Add(new MatterParty
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Name = name,
            Role = normalizedRole,
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"Recorded {normalizedRole} party '{name}' on matter '{matter.Name}'.";
    }

    [Description("Search the firm's matters for conflicts of interest against one or more names (clients, opposing parties, related entities). Read-only; finds hits even in restricted matters but does not reveal their details. Run this BEFORE opening a matter for a new client, then freeze the result with attest_conflict_check.")]
    public async Task<string> CheckConflicts(
        [Description("The names to check, separated by semicolons or newlines.")] string partyNames,
        CancellationToken cancellationToken = default)
    {
        var terms = ParseNames(partyNames);
        if (terms.Count == 0)
        {
            return "Provide at least one name to check.";
        }

        var hits = await SearchAsync(terms, cancellationToken);
        return hits.Count == 0
            ? $"No conflicts found for: {string.Join("; ", terms)}. Freeze this result with attest_conflict_check on the matter you are opening."
            : RenderHits(terms, hits) + "\nIf you proceed, record the decision with attest_conflict_check so the check is on the matter's tamper-evident record.";
    }

    [Description("Run a conflict search and freeze the result into the matter's tamper-evident, hash-chained attestation record (ADR-0006): what was searched, what was found, by whom, when — chained to the prior attestation so later edits are detectable. Side-effecting and requires approval.")]
    public async Task<string> AttestConflictCheck(
        [Description("The matter the attestation belongs to.")] string matterName,
        [Description("The names that were checked, separated by semicolons or newlines.")] string partyNames,
        CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId;
        if (userId is null)
        {
            return "Cannot attest without an authenticated user.";
        }

        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var terms = ParseNames(partyNames);
        if (terms.Count == 0)
        {
            return "Provide the names that were checked.";
        }

        var hits = await SearchAsync(terms, cancellationToken);
        var prior = await db.ConflictAttestations
            .Where(a => a.MatterId == matter.Id)
            .OrderByDescending(a => a.PerformedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var performedAt = DateTimeOffset.UtcNow;
        var snapshot = JsonSerializer.Serialize(new
        {
            searched = terms,
            matches = hits.Select(h => new { h.MatterName, h.PartyName, h.Role, h.Restricted }),
        });
        var attestation = new ConflictAttestation
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            AttestedByUserId = userId.Value,
            PerformedAt = performedAt,
            SearchTermsJson = JsonSerializer.Serialize(terms),
            DataSnapshotJson = snapshot,
            PriorAttestationHash = prior?.AttestationHash,
            AttestationHash = ConflictChain.ComputeHash(snapshot, prior?.AttestationHash, performedAt, userId.Value),
        };
        db.ConflictAttestations.Add(attestation);
        await db.SaveChangesAsync(cancellationToken);

        return $"Attested conflict check on matter '{matter.Name}': {terms.Count} name(s) searched, {hits.Count} hit(s). " +
               $"Attestation hash {attestation.AttestationHash[..16]}… (chain link {(prior is null ? "1 — chain start" : "chained to prior")}). " +
               "Verify the chain anytime with list_conflict_attestations.";
    }

    [Description("List a matter's conflict-check attestations (newest first) and verify the tamper-evident hash chain, reporting any broken link.")]
    public async Task<string> ListConflictAttestations(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var chain = await db.ConflictAttestations
            .Where(a => a.MatterId == matter.Id)
            .OrderBy(a => a.PerformedAt)
            .ToListAsync(cancellationToken);
        if (chain.Count == 0)
        {
            return $"Matter '{matter.Name}' has no conflict attestations yet. Record one with attest_conflict_check.";
        }

        var broken = ConflictChain.FindBrokenLink(chain);
        var sb = new StringBuilder($"Conflict attestations on matter '{matter.Name}' (newest first):\n");
        foreach (var a in Enumerable.Reverse(chain))
        {
            var searched = JsonSerializer.Deserialize<string[]>(a.SearchTermsJson) ?? [];
            sb.AppendLine($"- {a.PerformedAt:yyyy-MM-dd HH:mm}Z — searched {searched.Length} name(s): {string.Join("; ", searched)} — hash {a.AttestationHash[..16]}…");
        }

        sb.AppendLine(broken is null
            ? $"Chain integrity: VERIFIED — all {chain.Count} link(s) recompute correctly."
            : $"Chain integrity: BROKEN at link {broken.Value + 1} of {chain.Count} — the record has been altered since attestation.");
        return sb.ToString();
    }

    private sealed record ConflictHit(string MatterName, string PartyName, string Role, bool Restricted);

    private async Task<List<ConflictHit>> SearchAsync(List<string> terms, CancellationToken cancellationToken)
    {
        // Tenant-wide fetch of the search surface: parties plus each matter's client name. The wall
        // is deliberately NOT applied to the search itself — it is applied to the *rendering*.
        var matters = await db.Matters
            .Select(m => new { m.Id, m.Name, m.ClientName, m.RestrictedUserIdsJson })
            .ToListAsync(cancellationToken);
        var parties = await db.MatterParties
            .Select(p => new { p.MatterId, p.Name, p.Role })
            .ToListAsync(cancellationToken);

        var byMatter = matters.ToDictionary(m => m.Id);
        var hits = new List<ConflictHit>();
        foreach (var term in terms)
        {
            foreach (var p in parties)
            {
                if (!Matches(p.Name, term) || !byMatter.TryGetValue(p.MatterId, out var m))
                {
                    continue;
                }

                var restricted = !Matter.WallAllows(m.RestrictedUserIdsJson, currentUser.UserId);
                hits.Add(new ConflictHit(restricted ? "a restricted matter" : m.Name, p.Name, p.Role, restricted));
            }

            foreach (var m in matters)
            {
                if (m.ClientName is not null && Matches(m.ClientName, term))
                {
                    var restricted = !Matter.WallAllows(m.RestrictedUserIdsJson, currentUser.UserId);
                    hits.Add(new ConflictHit(restricted ? "a restricted matter" : m.Name, m.ClientName, "client", restricted));
                }
            }
        }

        return hits.DistinctBy(h => (h.MatterName, h.PartyName, h.Role)).ToList();
    }

    /// <summary>Symmetric case-insensitive containment: 'Initech' hits 'Initech LLC' and vice versa.</summary>
    internal static bool Matches(string candidate, string term) =>
        candidate.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        term.Contains(candidate, StringComparison.OrdinalIgnoreCase);

    internal static List<string> ParseNames(string names) =>
        names.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string RenderHits(List<string> terms, List<ConflictHit> hits)
    {
        var sb = new StringBuilder($"POTENTIAL CONFLICTS — {hits.Count} hit(s) for {string.Join("; ", terms)}:\n");
        foreach (var h in hits)
        {
            sb.AppendLine(h.Restricted
                ? $"- '{h.PartyName}' ({h.Role}) appears on a RESTRICTED matter — ask a firm admin inside the wall to review."
                : $"- '{h.PartyName}' ({h.Role}) on matter '{h.MatterName}'");
        }

        return sb.ToString();
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}
