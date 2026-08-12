using Cortex.Application.Approvals;
using Cortex.Core.Identity;
using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Persistence;
using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;

namespace Cortex.Modules.Legal;

/// <summary>
/// TODO(plenipo#153): restores the <b>requester's</b> identity while an approval-gated tool is being
/// re-executed, so the record names the human who asked for the write rather than the one who
/// approved it.
/// <para>
/// The platform parks a gated call as a <c>PendingApproval</c> carrying the requester's
/// <c>UserId</c>, <c>UserDisplay</c> and <c>TenantId</c>, but
/// <c>ApprovalExecutor.ExecuteAsync</c> re-invokes the tool on the <b>approver's</b> HTTP request
/// scope and never reads those fields. Every module tool resolves <see cref="ICurrentUser"/> from
/// that scope, so an approved write is attributed to the approver — which defeats invoice
/// separation of duties and leaves IOLTA movements naming the wrong human.
/// </para>
/// <para>
/// The platform already knows how to do this correctly elsewhere: <c>JobProcessor</c> restores the
/// enqueuer's identity into a fresh scope before running a job, and the WhatsApp channel does the
/// same. The approval path simply does not. Until it does, this shim performs the equivalent
/// restore from the product side, using only public API of the pinned
/// <c>0.1.0-alpha.14</c> packages.
/// </para>
/// <para>
/// <b>Deliberately narrow — this substitutes attribution, never authority.</b> Only
/// <c>UserId</c>/<c>Subject</c>/<c>DisplayName</c> are restored. The permission set and the tenant
/// are left exactly as the approver's request resolved them, because the requester's permissions
/// are not persisted on the approval record at alpha.14 (contrast <c>BackgroundJob</c>, which does
/// snapshot them). That is safe here: the platform authorizes the tool against the requester when
/// it parks the call, and does not re-check permissions at execution time, so nothing is widened.
/// It does mean a reader of the restored context sees the requester's identity next to the
/// approver's permission set — recorded rather than hidden, and the reason the real fix belongs in
/// the platform.
/// </para>
/// <para>
/// <b>Failure mode.</b> The trigger is the approve route the platform owns, so a platform that
/// renames it, or drains approvals off-request, silently stops restoring anything and reverts to
/// today's behaviour. That is a fail-open on a security-shaped control, which is why
/// <c>ApprovalIdentityTests</c> asserts it end to end over HTTP: if this shim stops firing, that
/// test goes red rather than the product quietly going back to naming the wrong human.
/// </para>
/// </summary>
internal static class ApprovalRequesterIdentity
{
    /// <summary>
    /// Wraps every tool so that, when it is invoked as part of resolving a parked approval, the
    /// requester's identity is restored onto the scope first. On an ordinary chat turn the path
    /// guard misses and the wrapper is a straight pass-through.
    /// </summary>
    public static IReadOnlyList<ModuleTool> RestoringRequesterIdentity(
        this IReadOnlyList<ModuleTool> tools, IServiceProvider scopedServices)
    {
        var wrapped = new ModuleTool[tools.Count];
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            wrapped[i] = new ModuleTool
            {
                ModuleId = tool.ModuleId,
                Name = tool.Name,
                Permission = tool.Permission,
                Function = new RequesterScopedFunction(tool.Function, scopedServices),
                RequiresApproval = tool.RequiresApproval,
                Audit = tool.Audit,
            };
        }

        return wrapped;
    }

    /// <summary>
    /// The id in <c>POST /api/chat/approvals/{id:guid}/approve</c>, or null when this request is
    /// not an approval resolution.
    /// </summary>
    internal static Guid? ApprovalBeingResolved(PathString path)
    {
        if (!path.HasValue)
        {
            return null;
        }

        var segments = path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("chat", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("approvals", StringComparison.OrdinalIgnoreCase) ||
            !segments[4].Equals("approve", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Guid.TryParse(segments[3], out var id) ? id : null;
    }

    private sealed class RequesterScopedFunction(AIFunction inner, IServiceProvider scopedServices) : AIFunction
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        // Every AIFunction member the base class exposes is forwarded, not just the ones the
        // approval path happens to read. This wrapper sits in front of EVERY Legal tool on EVERY
        // chat turn, so an unforwarded member does not degrade the shim — it silently empties that
        // member product-wide.
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // The swap is undone whatever the tool does — including throwing. Everything the
            // approve request performs AFTER the tool body is the approver's act, above all
            // IApprovalStore.ResolveAsync, whose IAuditable columns and entity-change audit row are
            // stamped from ICurrentUser at SaveChanges time. Leaving the requester in place there
            // would erase the approver from the only record that names them.
            var restoreApprover = await RestoreRequesterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                restoreApprover?.Invoke();
            }
        }

        /// <summary>
        /// Puts the requester on the scope for the duration of one tool call, returning the action
        /// that puts the approver back, or null when nothing was swapped.
        /// </summary>
        private async Task<Action?> RestoreRequesterAsync(CancellationToken cancellationToken)
        {
            var http = scopedServices.GetService<IHttpContextAccessor>()?.HttpContext;
            if (http is null || ApprovalBeingResolved(http.Request.Path) is not Guid approvalId)
            {
                return null; // an ordinary chat turn — the caller already is the actor
            }

            var context = scopedServices.GetRequiredService<RequestContext>();

            // Captured BEFORE the swap, and the swap is refused if the approver cannot be put back
            // faithfully: SetUser requires a non-null subject, so an unauthenticated scope has no
            // restore to offer. Refusing costs the attribution fix on a path that cannot reach here
            // (the approve route requires an authenticated approvals.manage holder) and guarantees
            // the stronger property — this shim never leaves an identity behind it did not find.
            if (context.UserId is not Guid approverId || context.Subject is not { } approverSubject)
            {
                Warn("the acting identity is incomplete, so the requester was not restored");
                return null;
            }

            var approverDisplay = context.DisplayName;

            var pending = await scopedServices.GetRequiredService<IApprovalStore>()
                .GetAsync(approvalId, cancellationToken).ConfigureAwait(false);

            if (pending?.UserId is not Guid requesterId || requesterId == approverId)
            {
                return null; // no recorded requester, or they approved their own call
            }

            // The requester's OIDC subject is not on the approval record, and SetUser needs one
            // (IsAuthenticated is derived from it), so it is read back from the platform's user
            // table. The tenant query filter keeps this in-tenant.
            var requester = await scopedServices.GetRequiredService<PlatformDbContext>().Users
                .Where(u => u.Id == requesterId)
                .Select(u => new { u.Subject, u.DisplayName })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (requester is null)
            {
                // Cannot name them faithfully, so do not invent an identity. This is the fail-open
                // branch that matters: the write proceeds attributed to the approver, i.e. the
                // defect #74 was filed on, quietly. Hence the warning.
                Warn($"approval {approvalId} names requester {requesterId}, who is not readable in "
                    + "this tenant; the write will be attributed to the approver");
                return null;
            }

            context.SetUser(requesterId, requester.Subject, pending.UserDisplay ?? requester.DisplayName);
            return () => context.SetUser(approverId, approverSubject, approverDisplay);
        }

        private void Warn(string message) =>
            scopedServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(ApprovalRequesterIdentity))
                .LogWarning("Approval requester identity: {Reason}.", message);
    }
}
