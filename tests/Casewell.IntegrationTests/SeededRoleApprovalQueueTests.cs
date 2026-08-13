using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// The shipped role set has to be able to *work* the human-in-the-loop gate, not only trip it.
/// A gate that every seeded role can park a write onto and no seeded role can release is a gate
/// that turns every approval-gated write into a dead end — the failure #76 reported.
/// <para>
/// The separation-of-duties line is asserted here too, deliberately in both directions:
/// <c>firm-admin</c> holds <c>legal.manage</c>, so it can already write the record directly
/// through the hand-edit surface with no approval at all — releasing a parked write grants it no
/// authority it did not already have. <c>paralegal</c> deliberately withholds <c>legal.manage</c>,
/// so granting it the queue would let the role that parks a write be the only signature on it.
/// It stays refused, and this test is what makes that a decision rather than an oversight.
/// </para>
/// Everything goes through <see cref="IntegrationFixture.AdminClient"/>, the only path that runs
/// the real RBAC + approval pipeline.
/// </summary>
[Collection("api")]
public sealed class SeededRoleApprovalQueueTests(IntegrationFixture fixture)
{
    private const string ToolName = "record_trust_deposit";

    [Fact]
    public async Task Firm_admin_can_read_and_release_the_write_it_parked()
    {
        using var admin = fixture.AdminClient();
        using var firmAdmin = fixture.AdminClient(roles: "firm-admin", subject: "fa-user");

        await SeedMatterAsync(firmAdmin, "Queue Role Co - Retainer");

        // Anything already queued by another case in this collection is not ours to touch.
        var before = await PendingIdsAsync(admin);

        var reply = await ParkGatedWriteAsync(
            firmAdmin, "Record a 5000 retainer deposit into trust on Queue Role Co - Retainer");
        Assert.Contains("requires human approval", reply);

        // 1. The queue is readable by the role that parked the write. This is the #76 regression:
        //    before the grant this is 403 and the firm can never see its own pending work.
        var queue = await firmAdmin.GetAsync("/api/chat/approvals");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        var pending = await queue.Content.ReadFromJsonAsync<JsonElement>();
        var id = pending.EnumerateArray()
            .Where(a => a.GetProperty("toolName").GetString() == ToolName)
            .Select(a => a.GetProperty("id").GetGuid())
            .Single(candidate => !before.Contains(candidate));

        // 2. And releasable by it — a queue you can read but not act on is the same dead end.
        var approved = await firmAdmin.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        var outcome = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Paralegal_parks_a_write_but_may_not_release_it()
    {
        using var paralegal = fixture.AdminClient(roles: "paralegal", subject: "pl-user");

        // Separation of duties, asserted rather than assumed: a paralegal can put work in front of
        // a human (add_task is enough to prove the role reaches the pipeline) but may not sign it
        // off. This test goes red the day someone widens the paralegal baseline by accident.
        var queue = await paralegal.GetAsync("/api/chat/approvals");
        Assert.Equal(HttpStatusCode.Forbidden, queue.StatusCode);

        var approve = await paralegal.PostAsJsonAsync(
            $"/api/chat/approvals/{Guid.NewGuid()}/approve", new { });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

        var reject = await paralegal.PostAsJsonAsync(
            $"/api/chat/approvals/{Guid.NewGuid()}/reject", new { });
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
    }

    private static async Task<HashSet<Guid>> PendingIdsAsync(HttpClient client)
    {
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        return pending.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task SeedMatterAsync(HttpClient client, string name)
    {
        // The hand-edit surface (legal.manage) — the very authority that makes releasing a parked
        // write no escalation for firm-admin. Seeding through the gated create_matter tool would
        // need an approval per fixture row.
        var response = await client.PostAsJsonAsync("/api/legal/matters", new
        {
            name,
            clientName = name.Split(" - ")[0],
            practiceArea = "Corporate",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> ParkGatedWriteAsync(HttpClient client, string message)
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

        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "RUN_ERROR");
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "CUSTOM" &&
            e.GetProperty("name").GetString() == "approval_required");

        return string.Concat(events
            .Where(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
            .Select(e => e.GetProperty("delta").GetString()));
    }
}
