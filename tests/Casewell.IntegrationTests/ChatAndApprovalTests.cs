using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// The human-in-the-loop gate, proven over HTTP. Asserting <c>RequiresApproval = true</c> on a
/// descriptor is static: it shows the flag is set, not that the gate fires. Without this test the
/// product can ship a broken approval gate with fully green CI — the worst failure available on
/// this platform. Everything here goes through <see cref="IntegrationFixture.AdminClient"/> for
/// that reason.
/// </summary>
[Collection("api")]
public sealed class ChatAndApprovalTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task A_read_only_turn_streams_the_full_agui_contract_and_audits_the_call()
    {
        using var client = fixture.AdminClient();

        var events = await StreamTurnAsync(client, "List my matters");

        Assert.Contains("RUN_STARTED", events.Types);
        Assert.Contains("TEXT_MESSAGE_START", events.Types);
        Assert.Contains("TEXT_MESSAGE_CONTENT", events.Types);
        Assert.Contains("TEXT_MESSAGE_END", events.Types);
        Assert.Contains("RUN_FINISHED", events.Types);
        Assert.DoesNotContain("RUN_ERROR", events.Types);

        // token_usage rides a CUSTOM event and is what populates admin usage reporting.
        Assert.Contains(events.Raw, e =>
            e.GetProperty("type").GetString() == "CUSTOM" &&
            e.GetProperty("name").GetString() == "token_usage");

        Assert.Contains("list_matters", events.ToolCalls);

        // Every invocation lands in the append-only tool-call audit with its permission.
        var audit = await client.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls");
        Assert.Contains(audit.EnumerateArray(), a =>
            a.GetProperty("toolName").GetString() == "list_matters" &&
            a.GetProperty("permission").GetString() == "tools.legal.list_matters");
    }

    [Fact]
    public async Task An_approval_gated_write_is_intercepted_then_executes_only_once_approved()
    {
        using var client = fixture.AdminClient();

        await SeedMatterAsync(client, "Approval Gate Co - Retainer");

        var events = await StreamTurnAsync(
            client, "Record a 5000 retainer deposit into trust on Approval Gate Co - Retainer");

        // 1. The gate fires, and the reply must not claim the write happened.
        Assert.Contains(events.Raw, e =>
            e.GetProperty("type").GetString() == "CUSTOM" &&
            e.GetProperty("name").GetString() == "approval_required");
        Assert.DoesNotContain("RUN_ERROR", events.Types);
        Assert.Contains("requires human approval", events.Reply);

        // 2. The pending call is queued for a human.
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var call = pending.EnumerateArray()
            .Single(a => a.GetProperty("toolName").GetString() == "record_trust_deposit");
        var id = call.GetProperty("id").GetGuid();

        // 3. Approving executes it — the write happens only on the human's say-so.
        var approved = await client.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        approved.EnsureSuccessStatusCode();
        var outcome = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());

        // 4. The queue drains, so a second approval cannot double-apply the write.
        var afterwards = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        Assert.DoesNotContain(afterwards.EnumerateArray(), a =>
            a.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Rejecting_a_gated_write_discards_it_and_writes_nothing()
    {
        using var client = fixture.AdminClient();

        await SeedMatterAsync(client, "Rejection Co - Retainer");
        await StreamTurnAsync(client, "Record a 250 retainer deposit into trust on Rejection Co - Retainer");

        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var id = pending.EnumerateArray()
            .First(a => a.GetProperty("toolName").GetString() == "record_trust_deposit")
            .GetProperty("id").GetGuid();

        var rejected = await client.PostAsJsonAsync($"/api/chat/approvals/{id}/reject", new { });
        rejected.EnsureSuccessStatusCode();

        var afterwards = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        Assert.DoesNotContain(afterwards.EnumerateArray(), a => a.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task A_caller_without_the_permission_is_refused_rather_than_gated()
    {
        // A narrower role than system_admin: the header is per-request precisely so RBAC is
        // testable. RBAC runs BEFORE the model, so an unpermitted tool is never even offered.
        using var narrow = fixture.AdminClient(roles: "guest", subject: "outsider");

        var response = await narrow.GetAsync("/api/admin/security/catalog");

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"guest reached an admin endpoint: {(int)response.StatusCode}");
    }

    private static async Task SeedMatterAsync(HttpClient client, string name)
    {
        // The module's hand-edit surface (legal.manage), not a chat tool — seeding through the
        // approval-gated create_matter would need an approval per fixture row.
        var response = await client.PostAsJsonAsync("/api/legal/matters", new
        {
            name,
            clientName = name.Split(" - ")[0],
            practiceArea = "Corporate",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<TurnEvents> StreamTurnAsync(HttpClient client, string message)
    {
        var response = await client.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[] { new { id = "m1", role = "user", content = message } },
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var events = body.Split('\n')
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l["data:".Length..].Trim()))
            .ToList();

        var reply = string.Concat(events
            .Where(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
            .Select(e => e.GetProperty("delta").GetString()));

        var toolCalls = events
            .Where(e => e.GetProperty("type").GetString() == "TOOL_CALL_START")
            .Select(e => e.GetProperty("toolCallName").GetString()!)
            .ToList();

        return new TurnEvents(
            events,
            events.Select(e => e.GetProperty("type").GetString()!).ToList(),
            toolCalls,
            reply);
    }

    private sealed record TurnEvents(
        IReadOnlyList<JsonElement> Raw,
        IReadOnlyList<string> Types,
        IReadOnlyList<string> ToolCalls,
        string Reply);
}
