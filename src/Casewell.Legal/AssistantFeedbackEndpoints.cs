using Cortex.Application.Authorization;
using Cortex.Application.Conversations;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Thumbs-up/down on assistant responses, and the firm-wide readout built from them — SPEC's
/// "AI helpfulness" metric, which is otherwise unmeasurable.
/// </summary>
/// <remarks>
/// <para>
/// Not approval-gated, deliberately: rating a reply is the reader commenting on their own turn,
/// not the agent proposing a write. A gate here would put an attorney between a reader and their
/// own thumbs-down, which suppresses precisely the signal the metric exists to catch.
/// </para>
/// <para>
/// RBAC plus ownership is the whole gate. <c>chat.use</c> is reused rather than a new permission
/// string invented — both shipped roles already hold it, and you may rate the assistant exactly
/// when you are allowed to talk to it. Ownership is enforced separately and is the real control:
/// a conversation belongs to one user, so nobody can rate — or skew the metric through — someone
/// else's thread.
/// </para>
/// <para>
/// <b>The summary returns counts, never the complaint text, and that is load-bearing.</b> A thumbs-down
/// comment is 2,000 characters of attorney free text about a conversation that may concern a walled
/// matter — the first draft of this endpoint returned the 20 most recent firm-wide to every holder
/// of <c>legal.matters.view</c> (which <c>paralegal</c> holds), with no author scope, no matter scope
/// and no <see cref="Matter.WallAllows"/> call, making it the module's only unfiltered read. It was
/// also self-demonstrating: the complaint it echoed named a matter. The text is still persisted, so
/// no signal is lost — surfacing it needs a matter-scoped, wall-filtered surface, which is #67.
/// Do not re-add it here.
/// </para>
/// </remarks>
internal static class AssistantFeedbackEndpoints
{
    /// <summary>Reused platform permission — talking to the assistant and rating it are the same right.</summary>
    private const string UseChat = "chat.use";

    public static void MapAssistantFeedbackEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/assistant/feedback", async (
                AssistantFeedbackRequest body,
                LegalDbContext db,
                IConversationStore conversations,
                ITenantContext tenant,
                ICurrentUser currentUser,
                CancellationToken cancellationToken) =>
            {
                var messageId = body.MessageId?.Trim();
                if (string.IsNullOrEmpty(messageId))
                {
                    return Results.BadRequest(new { error = "A rating needs the messageId of the response it is about." });
                }

                // The conversation is the ownership boundary, and this check is the ONLY thing
                // enforcing it. Verified by mutation, not assumed: with the comparison below
                // deleted, a second user rated another user's conversation successfully. So
                // IConversationStore.FindAsync is tenant-scoped but NOT user-scoped, even though
                // GET /api/chat/conversations is (a stranger's listing comes back empty). Do not
                // "simplify" this away on the strength of that listing.
                var conversation = await conversations.FindAsync(body.ConversationId, cancellationToken);
                if (conversation is null)
                {
                    return Results.NotFound(new { error = "No such conversation." });
                }

                if (currentUser.UserId is null || conversation.UserId != currentUser.UserId)
                {
                    return Results.Forbid();
                }

                // Keyed on (message, reader) to match the unique index, NOT on the conversation: a
                // caller may therefore pair a conversation they own with any messageId they like.
                // That is bounded — one row per reader per message either way, so the metric cannot
                // be stuffed — but it is not the same as "the message belongs to that conversation",
                // and the guarantee should not be described as more than it is.
                var existing = await db.AssistantFeedback.FirstOrDefaultAsync(
                    f => f.MessageId == messageId && f.UserId == currentUser.UserId, cancellationToken);
                if (existing is null)
                {
                    existing = new AssistantFeedback
                    {
                        TenantId = tenant.RequireTenantId(),
                        ConversationId = body.ConversationId,
                        MessageId = messageId,
                        UserId = currentUser.UserId,
                        UserDisplay = currentUser.DisplayName,
                    };
                    db.AssistantFeedback.Add(existing);
                }

                existing.Helpful = body.Helpful;
                existing.Comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
                await db.SaveChangesAsync(cancellationToken);

                return Results.Ok(new AssistantFeedbackDto(
                    existing.Id, existing.ConversationId, existing.MessageId, existing.Helpful, existing.Comment));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(UseChat))
            .WithName("Legal_RateAssistantResponse");

        group.MapGet("/assistant/feedback/summary", async (
                LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var helpful = await db.AssistantFeedback.CountAsync(f => f.Helpful, cancellationToken);
                var unhelpful = await db.AssistantFeedback.CountAsync(f => !f.Helpful, cancellationToken);

                // Counts only, deliberately — see the class remarks. The complaint free text is
                // still stored; it is simply not readable through this permission.
                return Results.Ok(new AssistantHelpfulnessDto(
                    helpful, unhelpful, helpful + unhelpful,
                    AssistantHelpfulness.Score(helpful, unhelpful)));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(LegalModule.ViewMatters))
            .WithName("Legal_GetAssistantHelpfulness");
    }
}

/// <param name="ConversationId">The conversation the response was streamed in.</param>
/// <param name="MessageId">The AG-UI <c>messageId</c> of the rated assistant message.</param>
/// <param name="Helpful">Thumbs up (true) or down (false).</param>
/// <param name="Comment">Optional free text — why.</param>
internal sealed record AssistantFeedbackRequest(
    Guid ConversationId, string? MessageId, bool Helpful, string? Comment);

internal sealed record AssistantFeedbackDto(
    Guid Id, Guid ConversationId, string MessageId, bool Helpful, string? Comment);

/// <param name="Score">1–5, or null when nothing has been rated yet.</param>
internal sealed record AssistantHelpfulnessDto(
    int Helpful, int Unhelpful, int Total, double? Score);
