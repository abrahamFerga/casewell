using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// Thumbs-up/down on a real assistant response, over HTTP, through the real pipeline. The
/// identifiers are taken from a live AG-UI stream rather than invented, because the whole point of
/// the feature is that a chat client can rate the response it just watched arrive.
/// </summary>
[Collection("api")]
public sealed class AssistantFeedbackTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task A_response_can_be_rated_and_the_rating_moves_the_helpfulness_score()
    {
        using var client = fixture.AdminClient(subject: "rating-attorney");

        var turn = await StreamTurnAsync(client, "List my matters");
        Assert.DoesNotContain("RUN_ERROR", turn.Types);

        var conversationId = await LatestConversationIdAsync(client);

        var posted = await client.PostAsJsonAsync("/api/legal/assistant/feedback", new
        {
            conversationId,
            messageId = turn.MessageId,
            helpful = true,
        });
        posted.EnsureSuccessStatusCode();
        var recorded = await posted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turn.MessageId, recorded.GetProperty("messageId").GetString());
        Assert.True(recorded.GetProperty("helpful").GetBoolean());

        var summary = await client.GetFromJsonAsync<JsonElement>("/api/legal/assistant/feedback/summary");
        Assert.True(summary.GetProperty("total").GetInt32() >= 1);
        Assert.True(summary.GetProperty("helpful").GetInt32() >= 1);
        // Something has been rated, so the score is a number rather than null.
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("score").ValueKind);
    }

    [Fact]
    public async Task Rating_the_same_response_twice_updates_the_verdict_instead_of_stacking_it()
    {
        using var client = fixture.AdminClient(subject: "fickle-attorney");

        var turn = await StreamTurnAsync(client, "List my matters");
        var conversationId = await LatestConversationIdAsync(client);

        var before = await TotalsAsync(client);

        (await client.PostAsJsonAsync("/api/legal/assistant/feedback", new
        {
            conversationId, messageId = turn.MessageId, helpful = true,
        })).EnsureSuccessStatusCode();

        // Same reader, same message, changed mind — and a complaint attached.
        (await client.PostAsJsonAsync("/api/legal/assistant/feedback", new
        {
            conversationId, messageId = turn.MessageId, helpful = false,
            comment = "Missed the deadline I asked about.",
        })).EnsureSuccessStatusCode();

        var after = await TotalsAsync(client);

        // One rating exists, not two: the second click replaced the first.
        Assert.Equal(before.Total + 1, after.Total);
        Assert.Equal(before.Helpful, after.Helpful);
        Assert.Equal(before.Unhelpful + 1, after.Unhelpful);

        // The complaint text is stored but deliberately NOT readable here — the summary is counts
        // only, because this permission is not matter-scoped and the text can name a walled matter.
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/legal/assistant/feedback/summary");
        Assert.False(summary.TryGetProperty("recentComplaints", out _));
        Assert.DoesNotContain("Missed the deadline", summary.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rating_needs_a_message_to_be_about()
    {
        using var client = fixture.AdminClient(subject: "blank-rater");
        await StreamTurnAsync(client, "List my matters");
        var conversationId = await LatestConversationIdAsync(client);

        var response = await client.PostAsJsonAsync("/api/legal/assistant/feedback", new
        {
            conversationId, messageId = "   ", helpful = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_reader_cannot_rate_a_conversation_that_is_not_theirs()
    {
        // The ownership boundary, and the reason it matters: without it any user could inflate or
        // tank the firm's helpfulness metric through a thread they never saw.
        using var owner = fixture.AdminClient(subject: "owning-attorney");
        var turn = await StreamTurnAsync(owner, "List my matters");
        var conversationId = await LatestConversationIdAsync(owner);

        using var stranger = fixture.AdminClient(subject: "stranger-attorney");
        var before = await TotalsAsync(stranger);

        var response = await stranger.PostAsJsonAsync("/api/legal/assistant/feedback", new
        {
            conversationId, messageId = turn.MessageId, helpful = false,
        });

        // Forbidden specifically: a 404 or a 500 would also be "not successful", and either would
        // mean the ownership check was never the thing that stopped this.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await TotalsAsync(stranger);
        Assert.Equal(before.Total, after.Total);
    }

    private static async Task<(int Total, int Helpful, int Unhelpful)> TotalsAsync(HttpClient client)
    {
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/legal/assistant/feedback/summary");
        return (summary.GetProperty("total").GetInt32(),
                summary.GetProperty("helpful").GetInt32(),
                summary.GetProperty("unhelpful").GetInt32());
    }

    private static async Task<Guid> LatestConversationIdAsync(HttpClient client)
    {
        var conversations = await client.GetFromJsonAsync<JsonElement>("/api/chat/conversations");
        return conversations.EnumerateArray()
            .OrderByDescending(c => c.GetProperty("updatedAt").GetDateTimeOffset())
            .First()
            .GetProperty("id")
            .GetGuid();
    }

    private static async Task<TurnResult> StreamTurnAsync(HttpClient client, string message)
    {
        var response = await client.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[] { new { id = "m1", role = "user", content = message } },
        });
        response.EnsureSuccessStatusCode();

        var events = (await response.Content.ReadAsStringAsync())
            .Split('\n')
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l["data:".Length..].Trim()))
            .ToList();

        // The rated identifier comes off TEXT_MESSAGE_START — exactly what a chat client holds
        // when the reader clicks a thumb.
        var messageId = events
            .First(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_START")
            .GetProperty("messageId").GetString()!;

        return new TurnResult(events.Select(e => e.GetProperty("type").GetString()!).ToList(), messageId);
    }

    private sealed record TurnResult(IReadOnlyList<string> Types, string MessageId);
}
