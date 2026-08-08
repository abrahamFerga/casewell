using Cortex.Application.Authorization;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Casewell.Host;

/// <summary>
/// TODO(casewell#40): DELETE THIS FILE when Casewell moves off <c>Cortex 0.1.0-alpha.14</c>.
/// The tracker is casewell#40 ("Upgrade off the pre-rename Cortex.* packages onto Plenipo.*"),
/// NOT plenipo#40 — the platform issue of that number is an unrelated, already-merged npm
/// publish, so a literal reading of the old tag said "safe to delete" while the pin still stands.
///
/// A local shim for the gated-decision trail (#62). The platform already owns this surface —
/// <c>DisclosureEndpoints</c> serves <c>/api/platform/ai-decisions</c> and unions resolved
/// approvals with ungated tool-call audit rows — but that endpoint does not exist at the version
/// this product pins, so there is nothing to call. Verified at L1 against the platform repo:
/// <c>git ls-tree -r v0.1.0-alpha.14</c> contains no <c>DisclosureEndpoints</c> and no
/// <c>ai-decisions</c> route; both are present at platform HEAD.
///
/// What that leaves broken here is narrow and real. Approving a gated call runs it through
/// <c>ApprovalExecutor.ExecuteAsync</c>, which invokes the tool directly rather than through
/// <c>ToolInvocationMiddleware</c>, so no tool-call audit row is written for the execution — by
/// design, and the resolved <c>PendingApproval</c> row is the authoritative record instead. But at
/// alpha.14 <c>IApprovalStore</c> exposes only <c>ListPendingAsync</c> (filtered to
/// <c>Status == Pending</c>) and <c>GetAsync(id)</c>, so a resolved approval is readable through no
/// API at all. The decision is durable and invisible. For a product whose stated differentiator is
/// a tamper-evident trust trail, that is a shipped defect, which is why this shim exists rather
/// than waiting for the upgrade.
///
/// Deliberately NOT a reimplementation of the platform feed: it is read-only, it adds no entity and
/// no migration, and it unions nothing — ungated executions are already readable via
/// <c>/api/admin/audit/tool-calls</c>. It is the missing SELECT and nothing more, so deleting it
/// after the upgrade is a one-file change plus repointing callers at the platform route.
///
/// Tenant isolation is the platform's, not this file's: <c>PendingApproval</c> is a
/// <c>TenantEntityBase</c> carrying a global query filter on <c>PlatformDbContext</c>, so the query
/// below cannot see another tenant's rows even though it names no tenant.
/// </summary>
internal static class DecisionTrailEndpoints
{
    public static IEndpointRouteBuilder MapCasewellDecisionTrail(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/legal/ai-decisions", async (
                PlatformDbContext db,
                int? take,
                CancellationToken ct) =>
            {
                var limit = Math.Clamp(take ?? 100, 1, 500);

                // Pending rows are excluded on purpose: an action still awaiting a human is not a
                // decision, it is a queue item, and /api/chat/approvals already serves that.
                //
                // Deliberately NOT filtered to ModuleId == "legal" despite the /api/legal route:
                // Casewell is a single-module product, so every row IS a legal row, and filtering
                // would silently hide decisions if a second module ever landed. If one does, decide
                // then whether to scope this or move it to the platform's /api/platform route.
                var decided = await db.PendingApprovals
                    .Where(p => p.Status != ApprovalStatus.Pending)
                    .OrderByDescending(p => p.ResolvedAt ?? p.CreatedAt)
                    .Take(limit)
                    .Select(p => new DecisionDto(
                        p.Id,
                        p.ModuleId,
                        p.ToolName,
                        p.ArgumentsJson,
                        p.Status.ToString(),
                        p.Result,
                        p.Error,
                        p.UserDisplay,
                        p.ResolvedAt ?? p.CreatedAt))
                    .ToListAsync(ct);

                return Results.Ok(decided);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewAuditLog))
            .WithName("Casewell_DecisionTrail")
            .WithTags("Disclosure");

        return app;
    }

    /// <summary>
    /// One resolved gated call. <c>requestedBy</c> is the user whose turn triggered the call — NOT
    /// the approver: alpha.14's <c>PendingApproval</c> has no <c>ResolvedByUserId</c> (added later,
    /// present at platform HEAD), so who pressed approve is genuinely not recorded at this version.
    /// The upgrade in #40 is what makes that answerable; this shim does not invent it.
    /// </summary>
    private sealed record DecisionDto(
        Guid Id,
        string ModuleId,
        string ToolName,
        string? ArgumentsJson,
        string Status,
        string? Result,
        string? Error,
        string? RequestedBy,
        DateTimeOffset ResolvedAt);
}
