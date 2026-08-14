using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Application.Auditing;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
/// An assertion that a record is ABSENT is only defensible if the record demonstrably exists
/// somewhere else — otherwise it pins a hole and calls it a design.
/// <see cref="Resolving_an_approval_is_appended_to_the_audit_database"/> supplies that other half by
/// observation rather than by reading platform source, so the pair states the contract in full:
/// not in the tool-call audit, but in the append-only entity-change audit.
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
        await StreamTurnAsync(client, "Record a 1200 retainer deposit into trust on 'Audit Shape Co - Retainer' for 'retainer received'");
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

    /// <summary>
    /// The claim this whole PR rests on, moved from inference to observation.
    ///
    /// The sibling test above ships a green, PERMANENT assertion that a gated execution is absent
    /// from the tool-call audit. That is only honest if the decision is captured somewhere durable,
    /// and until this test existed that "somewhere" was an L4 reading across three platform files
    /// (<c>ApprovalExecutor</c>, <c>IApprovalStore</c>, the <c>PendingApproval</c> base chain) with
    /// no read surface at <c>Cortex 0.1.0-alpha.14</c> to observe it through — the only audit routes
    /// are <c>AdminEndpoints.cs:1039</c> and <c>:1057</c>, neither of which returns entity changes.
    /// So this reads the audit store directly out of the running host's container, the same way
    /// <c>IntegrationFixture.EnsureTenantAsync</c> reaches <c>PlatformDbContext</c>.
    ///
    /// The mechanism, verified against source at the pinned tag and then confirmed here by running
    /// it: <c>ApprovalStore.ResolveAsync</c> mutates the <c>PendingApproval</c> and saves through
    /// <c>PlatformDbContext</c>; <c>InfrastructureSetup.cs:174-178</c> attaches
    /// <c>AuditInterceptor</c> to that context alone; the interceptor emits an
    /// <c>EntityChangeAuditEntry</c> for every changed entity except <c>RagChunk</c>; and
    /// <c>AuditLog.RecordEntityChangesAsync</c> writes it synchronously on the happy path, so there
    /// is nothing to poll for and this test needs no retry loop.
    ///
    /// Asserted in BOTH directions deliberately. A one-way "a Modified row exists" passes just as
    /// happily if the interceptor stamped every approval on creation, or if the query matched any
    /// row of any kind — so an approval that is still PENDING must have no Modified row, which is
    /// what makes this a proof that RESOLUTION is what gets recorded.
    /// </summary>
    [Fact]
    public async Task Resolving_an_approval_is_appended_to_the_audit_database()
    {
        using var client = fixture.AdminClient();

        await SeedMatterAsync(client, "Ledger Co - Retainer");
        await StreamTurnAsync(client, "Record a 2100 retainer deposit into trust on 'Ledger Co - Retainer' for 'retainer received'");
        var resolvedId = await ApproveAsync(client, "record_trust_deposit");

        // The negative control, created AFTER the approval above and left unresolved on purpose.
        // Order matters: ListPendingAsync is OrderByDescending(CreatedAt) and ApproveAsync takes
        // the first match, so seeding this one earlier would hand it to the approval above.
        await SeedMatterAsync(client, "Unresolved Co - Retainer");
        await StreamTurnAsync(client, "Record a 700 retainer deposit into trust on 'Unresolved Co - Retainer' for 'retainer received'");
        var pendingId = (await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals"))
            .EnumerateArray()
            .First(a => a.GetProperty("toolName").GetString() == "record_trust_deposit")
            .GetProperty("id").GetGuid();

        Assert.NotEqual(resolvedId, pendingId);

        using var scope = fixture.Factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // AuditDbContext carries no global query filter (it is a plain DbContext — see its
        // OnModelCreating), so this reads every tenant's rows and the EntityId match is doing all
        // of the discrimination. EntityId is a string: AuditInterceptor.TryGetId stringifies the
        // primary key.
        var approvalChanges = await audit.EntityChanges
            .Where(e => e.EntityType == nameof(PendingApproval))
            .ToListAsync();

        Assert.NotEmpty(approvalChanges);

        var resolution = Assert.Single(
            approvalChanges,
            e => e.EntityId == resolvedId.ToString() && e.Kind == EntityChangeKind.Modified);

        // The row has to name the TRANSITION, not merely exist. A Modified entry whose ChangesJson
        // never mentions Status would mean something else about the approval moved (ResolvedAt and
        // Result also change on this save), and an audit trail that records "something changed" is
        // not a decision record. AuditInterceptor.SerializeChanges emits
        // { property: [originalValue, currentValue] }, and System.Text.Json writes the enum as its
        // numeric value — ApprovalStatus.Pending = 0, Executed = 1 at the pinned tag.
        var changesJson = resolution.ChangesJson;
        Assert.False(string.IsNullOrWhiteSpace(changesJson), "the resolution was audited with no change map");

        using var changes = JsonDocument.Parse(changesJson!);
        Assert.True(
            changes.RootElement.TryGetProperty("Status", out var status),
            $"the audited change map does not mention Status: {changesJson}");

        Assert.Equal((int)ApprovalStatus.Pending, status[0].GetInt32());
        Assert.Equal((int)ApprovalStatus.Executed, status[1].GetInt32());

        Assert.DoesNotContain(
            approvalChanges,
            e => e.EntityId == pendingId.ToString() && e.Kind == EntityChangeKind.Modified);
    }

    [Fact]
    public async Task An_approved_write_is_readable_in_the_decision_trail_with_its_outcome()
    {
        using var client = fixture.AdminClient();

        await SeedMatterAsync(client, "Trail Co - Retainer");
        await StreamTurnAsync(client, "Record a 3400 retainer deposit into trust on 'Trail Co - Retainer' for 'retainer received'");
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
        Assert.NotNull(decision.GetProperty("resolvedAt").GetString());

        // "Non-empty" is not an outcome. This test is named for the outcome it reports, and a
        // non-empty check passes just as happily on a tool that FAILED — which is what was
        // happening until the quoted phrasing above. MockChatClient maps quoted spans to string
        // params in parameter order and otherwise dumps the whole message into the first one, so
        // the old unquoted wording sent matterName="Record a 3400 retainer deposit into trust on
        // Trail Co - Retainer" and the tool returned "No matter named '…'". Status is still
        // Executed — ApprovalStatus.Failed means the tool THREW, and a business-level refusal is a
        // normal return — so nothing went red and the decision trail was being proven against
        // deposits that never landed.
        var result = decision.GetProperty("result").GetString();
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.DoesNotContain("No matter named", result!, StringComparison.Ordinal);

        // The matter AND the money, not just "it didn't error": the matter name proves the quoted
        // span reached matterName, and the formatted amount proves the numeric argument was mapped
        // too — TrustAccounting.Money renders 3400 as "$3,400.00".
        Assert.Contains("Trail Co - Retainer", result!, StringComparison.Ordinal);
        Assert.Contains("$3,400.00", result!, StringComparison.Ordinal);

        // A decision still awaiting a human is not a decision, and must not appear.
        await SeedMatterAsync(client, "Still Pending Co - Retainer");
        await StreamTurnAsync(client, "Record a 90 retainer deposit into trust on 'Still Pending Co - Retainer' for 'retainer received'");

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
        await StreamTurnAsync(firmA, "Record a 1500 retainer deposit into trust on 'Firm A Holdings - Retainer' for 'retainer received'");
        var firmAId = await ApproveAsync(firmA, "record_trust_deposit");

        await SeedMatterAsync(firmB, "Firm B Ventures - Retainer");
        await StreamTurnAsync(firmB, "Record a 2600 retainer deposit into trust on 'Firm B Ventures - Retainer' for 'retainer received'");
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
