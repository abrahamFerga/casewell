using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// One reader's verdict on one assistant response — the thumbs-up/down instrumentation behind
/// SPEC's "AI helpfulness" metric. Scoped to the message, not the conversation, because a single
/// thread routinely mixes a good brief with a bad draft and a per-thread rating would average
/// that signal away.
/// </summary>
/// <remarks>
/// Deliberately NOT approval-gated: rating a reply is the reader speaking about their own turn,
/// not the agent proposing an action, and a gate on feedback would suppress exactly the negative
/// ratings the metric exists to surface. RBAC is the whole gate, plus an ownership check — a user
/// may only rate a conversation that is theirs.
/// </remarks>
public sealed class AssistantFeedback : TenantEntityBase
{
    /// <summary>The platform conversation the rated message belongs to.</summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    /// The rated assistant message, as the AG-UI stream identifies it (<c>msg_019f…</c>). A string,
    /// not a Guid, and verified against a live stream: <c>TEXT_MESSAGE_START</c> carries
    /// <c>messageId</c> in that prefixed form, and it is the only response identifier a chat client
    /// holds at the moment the reader clicks a thumb.
    /// </summary>
    public required string MessageId { get; set; }

    /// <summary>Who rated it. One rating per user per message; re-rating overwrites.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Display-name snapshot at rating time, so a readout needs no user join.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Thumbs up (true) or thumbs down (false).</summary>
    public bool Helpful { get; set; }

    /// <summary>Optional free text — the part that tells you *why* a thumbs-down happened.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Turns thumbs into the 1–5 figure SPEC states the metric in ("AI helpfulness &gt;= 4.0/5").
/// Pure and separately testable: the mapping is a product decision, not an implementation detail,
/// and it is the one part of the feature that can be silently wrong while everything still runs.
/// </summary>
public static class AssistantHelpfulness
{
    /// <summary>
    /// Linear map of the helpful ratio onto 1–5: all thumbs-down scores 1.0, all thumbs-up 5.0,
    /// an even split 3.0. Returns null when nothing has been rated — an unrated assistant has no
    /// score, and reporting 0 (or 3) would let "nobody answered" read as a measurement.
    /// </summary>
    public static double? Score(int helpful, int unhelpful)
    {
        var total = helpful + unhelpful;
        if (total <= 0)
        {
            return null;
        }

        return 1d + (4d * helpful / total);
    }
}
