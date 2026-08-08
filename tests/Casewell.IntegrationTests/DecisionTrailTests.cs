using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// The gated-decision trail (#62), proven over HTTP against a real Postgres.
///
/// The sweep that filed #62 observed — correctly — that approving a gated write leaves NO row in
/// <c>/api/admin/audit/tool-calls</c>. Its diagnosis ("the audit log is broken") is wrong, and
/// <see cref="An_approved_write_is_deliberately_absent_from_the_tool_call_audit"/> pins the real
/// design so nobody re-derives it a fourth time: the platform records a gated call's execution on
/// the <c>PendingApproval</c> row, not in the tool-call audit, because the re-execution happens
/// outside <c>ToolInvocationMiddleware</c>. Counting both would double-report one action.
///
/// The genuine defect is narrower and is what the second test covers: at the pinned
/// <c>Cortex 0.1.0-alpha.14</c> there is no read surface that returns a RESOLVED approval, so the
/// decision is durable but invisible — <c>IApprovalStore.ListPendingAsync</c> filters
/// <c>Status == Pending</c>, and the platform's own union feed (<c>/api/platform/ai-decisions</c>)
/// does not exist until after this version. For a legal product whose differentiator is a
/// tamper-evident trust trail, an invisible decision is a shipped defect.
/// </summary>
[Collection("api")]
public sealed class DecisionTrailTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task An_approved_write_is_deliberately_absent_from_the_tool_call_audit()
    {
        using var client = fixture.AdminClient();

        await SeedMatterAsync(client, "Audit Shape Co - Retainer");
        await StreamTurnAsync(client, "Record a 1200 retainer deposit into trust on Audit Shape Co - Retainer");
        var id = await ApproveAsync(client, "record_trust_deposit");

        // The blocked attempt IS audited; the execution that followed is not. This asserts the
        // platform's actual contract rather than the one #62 assumed — if a future platform
        // version starts auditing the execution too, this test goes red and forces a re-read.
        var audit = await client.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls");
        var rows = audit.EnumerateArray()
            .Where(a => a.GetProperty("toolName").GetString() == "record_trust_deposit")
            .ToList();

        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            // TryGetProperty, not GetProperty: if the audit DTO ever stops serializing its null
            // errors, GetProperty throws KeyNotFoundException and this test dies with a JSON
            // plumbing error instead of printing the contract message it exists to print. An
            // ABSENT "error" asserts exactly what a null one does — the execution succeeded — so
            // it must fail the same way and say the same thing, not crash.
            var blocked = r.TryGetProperty("error", out var error)
                && error.ValueKind != JsonValueKind.Null;

            Assert.True(
                blocked,
                "a gated execution appeared in the tool-call audit — the platform contract changed");
        });

        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task An_approved_write_is_readable_in_the_decision_trail_with_its_outcome()
    {
        using var client = fixture.AdminClient();

        await SeedMatterAsync(client, "Trail Co - Retainer");
        await StreamTurnAsync(client, "Record a 3400 retainer deposit into trust on Trail Co - Retainer");
        var id = await ApproveAsync(client, "record_trust_deposit");

        // ?take=500 (the endpoint's clamp ceiling) rather than the default 100. This fixes no
        // live flake: rows come back newest-resolved-first, so the one just approved is at the
        // head of any window. What it removes is the test's *dependence* on that ordering — with
        // the default window, changing the OrderByDescending would turn this green-by-luck the
        // moment the dev tenant carries more than 100 resolved approvals.
        var trail = await client.GetFromJsonAsync<JsonElement>("/api/legal/ai-decisions?take=500");

        var decision = Assert.Single(
            trail.EnumerateArray(),
            d => d.GetProperty("id").GetGuid() == id);

        Assert.Equal("record_trust_deposit", decision.GetProperty("toolName").GetString());
        Assert.Equal("Executed", decision.GetProperty("status").GetString());
        Assert.Equal("legal", decision.GetProperty("moduleId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("result").GetString()));
        Assert.NotNull(decision.GetProperty("resolvedAt").GetString());

        // A decision still awaiting a human is not a decision, and must not appear.
        await SeedMatterAsync(client, "Still Pending Co - Retainer");
        await StreamTurnAsync(client, "Record a 90 retainer deposit into trust on Still Pending Co - Retainer");

        var pendingIds = (await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals"))
            .EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToHashSet();
        Assert.NotEmpty(pendingIds);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/legal/ai-decisions?take=500");
        Assert.DoesNotContain(after.EnumerateArray(), d => pendingIds.Contains(d.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task The_decision_trail_is_gated_on_the_audit_view_permission()
    {
        // paralegal is a real seeded role and holds no platform.audit.view — a non-wildcard role,
        // so this proves the gate rather than the absence of a grant on a synthetic one.
        using var narrow = fixture.AdminClient(roles: "paralegal", subject: "para-trail");

        var response = await narrow.GetAsync("/api/legal/ai-decisions");

        // Pinned to Forbidden alone, not Forbidden-or-Unauthorized. The looser form would also
        // pass if the caller were merely unauthenticated, which proves nothing about the
        // permission; this client IS authenticated and is refused on the grant it lacks.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task One_firms_decision_trail_never_contains_another_firms_decisions()
    {
        // The invariant this pins is the whole reason a legal product may hold two firms at once:
        // Firm A's managing partner must not be able to read that Firm B approved a trust deposit.
        // /api/legal/ai-decisions names no tenant in its WHERE clause — it leans entirely on
        // PlatformDbContext's global filter over PendingApproval (a TenantEntityBase). Until this
        // test existed that lean was an L4 source reading, not an observed behaviour: the earlier
        // cross-tenant probe used an unseeded slug, which RequestEnricher cannot resolve, so it was
        // refused at RBAC with 403 and the filter was never reached. Both tenants here are real.
        await fixture.EnsureTenantAsync("isolation-co", "Isolation Co Law");

        using var firmA = fixture.AdminClient(subject: "partner-a");
        using var firmB = fixture.AdminClient(subject: "partner-b", tenant: "isolation-co");

        await SeedMatterAsync(firmA, "Firm A Holdings - Retainer");
        await StreamTurnAsync(firmA, "Record a 1500 retainer deposit into trust on Firm A Holdings - Retainer");
        var firmAId = await ApproveAsync(firmA, "record_trust_deposit");

        await SeedMatterAsync(firmB, "Firm B Ventures - Retainer");
        await StreamTurnAsync(firmB, "Record a 2600 retainer deposit into trust on Firm B Ventures - Retainer");
        var firmBId = await ApproveAsync(firmB, "record_trust_deposit");

        Assert.NotEqual(firmAId, firmBId);

        // Asserted in BOTH directions on purpose. A one-way check passes just as happily when the
        // filter is applied with a hardcoded tenant, or when the second tenant simply has no rows;
        // requiring each firm to see exactly its own decision and none of the other's is what makes
        // this a proof of isolation rather than of emptiness.
        var trailA = await ReadDecisionIdsAsync(firmA);
        Assert.Contains(firmAId, trailA);
        Assert.DoesNotContain(firmBId, trailA);

        var trailB = await ReadDecisionIdsAsync(firmB);
        Assert.Contains(firmBId, trailB);
        Assert.DoesNotContain(firmAId, trailB);
    }

    private static async Task<HashSet<Guid>> ReadDecisionIdsAsync(HttpClient client)
    {
        var trail = await client.GetFromJsonAsync<JsonElement>("/api/legal/ai-decisions?take=500");
        return trail.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task<Guid> ApproveAsync(HttpClient client, string toolName)
    {
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var id = pending.EnumerateArray()
            .First(a => a.GetProperty("toolName").GetString() == toolName)
            .GetProperty("id").GetGuid();

        var approved = await client.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        approved.EnsureSuccessStatusCode();
        var outcome = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());

        return id;
    }

    private static async Task SeedMatterAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/legal/matters", new
        {
            name,
            clientName = name.Split(" - ")[0],
            practiceArea = "Corporate",
        });
        response.EnsureSuccessStatusCode();
    }

    private static async Task StreamTurnAsync(HttpClient client, string message)
    {
        var response = await client.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[] { new { id = "m1", role = "user", content = message } },
        });
        response.EnsureSuccessStatusCode();
        await response.Content.ReadAsStringAsync();
    }
}
