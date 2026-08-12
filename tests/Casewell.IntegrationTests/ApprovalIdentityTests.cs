using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// Who the system believes performed an approval-gated write.
/// <para>
/// A parked call is requested by one human and released by another. If it executes under the
/// <b>approver's</b> identity, two controls fail silently: invoice separation of duties (the
/// preparer and the approver become the same person, so "nobody approves their own bill" reports
/// satisfied while one human drove both ends), and IOLTA attribution under ABA Model Rule 1.15
/// (the client-money ledger names the wrong person).
/// </para>
/// <para>
/// Everything here goes over HTTP through <see cref="IntegrationFixture.AdminClient"/> with two
/// genuinely different provisioned subjects. <c>AuthorizedScopeAsync</c> would be useless: it sets
/// the identity by hand and skips the pipeline, so it can never prove whose identity the pipeline
/// actually resolves.
/// </para>
/// </summary>
[Collection("api")]
public sealed class ApprovalIdentityTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task An_approved_trust_movement_names_the_requester_not_the_approver()
    {
        using var requester = Client("requester-user", "Requesting Paralegal");
        using var approver = Client("approver-user", "Approving Partner");

        var requesterName = await DisplayNameAsync(requester);
        var approverName = await DisplayNameAsync(approver);

        // If the two subjects resolved to the same display the assertions below would pass for the
        // wrong reason, so the test states its own precondition.
        Assert.NotEqual(requesterName, approverName);

        const string matter = "Attribution Co - Retainer";
        await SeedMatterAsync(requester, matter);

        var before = await PendingIdsAsync(requester);

        // The Mock provider maps QUOTED spans onto string parameters in declaration order, so the
        // matter name is quoted; an unquoted sentence lands wholesale in matterName and the tool
        // then finds no matter at all.
        await StreamTurnAsync(requester, $"Record a trust deposit of 1500 on '{matter}'");

        // Take the approval THIS turn parked rather than the first one named record_trust_deposit —
        // the fixture is shared, so a leftover from another test would otherwise be approved here.
        var after = await PendingIdsAsync(requester);
        var id = Assert.Single(after.Except(before));

        var approved = await approver.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        approved.EnsureSuccessStatusCode();
        var outcome = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());

        var journal = await requester.GetFromJsonAsync<JsonElement>("/api/legal/trust-transactions");
        var row = journal.EnumerateArray()
            .Single(t => t.GetProperty("matterName").GetString() == matter);
        var who = row.GetProperty("who").GetString();

        // The client's money moved because the paralegal asked for it. The partner authorised it;
        // they did not perform it.
        Assert.Equal(requesterName, who);
        Assert.NotEqual(approverName, who);
    }

    [Fact]
    public async Task An_approved_invoice_records_the_requester_as_its_preparer()
    {
        using var requester = Client("requester-user", "Requesting Paralegal");
        using var approver = Client("approver-user", "Approving Partner");

        var requesterName = await DisplayNameAsync(requester);
        var approverName = await DisplayNameAsync(approver);
        Assert.NotEqual(requesterName, approverName);

        // The Mock provider copies the user message into the tool argument, so the matter is named
        // for the sentence that will select it.
        const string matter = "Draft invoice for Attribution Co";
        await SeedMatterAsync(requester, matter);

        var time = await requester.PostAsJsonAsync("/api/legal/time-entries", new
        {
            matterName = matter,
            hours = 4,
            description = "Review",
        });
        time.EnsureSuccessStatusCode();

        var before = await PendingIdsAsync(requester);
        await StreamTurnAsync(requester, matter);
        var after = await PendingIdsAsync(requester);

        var id = Assert.Single(after.Except(before));
        var approved = await approver.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        approved.EnsureSuccessStatusCode();
        Assert.Equal(
            "Executed",
            (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var invoices = await requester.GetFromJsonAsync<JsonElement>("/api/legal/invoices");
        var invoice = invoices.EnumerateArray()
            .Single(i => i.GetProperty("matterName").GetString() == matter);
        var preparedBy = invoice.GetProperty("preparedBy").GetString();

        // BillingTools.CanApprove compares PreparedByUserId with the approver. If the approver is
        // recorded as the preparer, that control can never fire.
        Assert.Equal(requesterName, preparedBy);
        Assert.NotEqual(approverName, preparedBy);
    }

    /// <summary>
    /// The identity swap is scoped to the tool body and nothing else.
    /// <para>
    /// <c>ApprovalEndpoints</c> calls <c>executor.ExecuteAsync</c> and then
    /// <c>store.ResolveAsync</c> on the <b>same</b> request scope, and <c>AuditInterceptor</c>
    /// stamps <c>IAuditable.UpdatedBy</c> from whoever <c>ICurrentUser</c> names at
    /// <c>SavingChangesAsync</c> time. So if the shim restores the requester and never puts the
    /// approver back, the approval row it just resolved — and its entity-change audit entry — are
    /// stamped with the requester, and the approver is recorded nowhere in the system on a path
    /// that moves client money. This asserts the opposite: the write is the requester's, the
    /// decision to release it stays the approver's.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resolving_an_approval_is_still_recorded_against_the_approver()
    {
        using var requester = Client("restore-requester", "Restoring Paralegal");
        using var approver = Client("restore-approver", "Restoring Partner");

        var requesterName = await DisplayNameAsync(requester);
        var approverName = await DisplayNameAsync(approver);
        Assert.NotEqual(requesterName, approverName);

        const string matter = "Restore Co - Retainer";
        await SeedMatterAsync(requester, matter);

        var before = await PendingIdsAsync(requester);
        await StreamTurnAsync(requester, $"Record a trust deposit of 1500 on '{matter}'");
        var id = Assert.Single((await PendingIdsAsync(requester)).Except(before));

        var approved = await approver.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        approved.EnsureSuccessStatusCode();

        // The tool ran as the requester — that is the fix this PR exists for, and it is asserted by
        // the two tests above. This one reads the row the SAME request wrote afterwards.
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using (scope)
        {
            var resolved = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .PendingApprovals.AsNoTracking().SingleAsync(a => a.Id == id);

            Assert.Equal(ApprovalStatus.Executed, resolved.Status);

            // Without a finally around the tool body, UpdatedBy reads "Restoring Paralegal" here.
            Assert.Equal(approverName, resolved.UpdatedBy);
            Assert.NotEqual(requesterName, resolved.UpdatedBy);
        }
    }

    /// <summary>
    /// Attribution moves across a real authority boundary; authority does not move with it.
    /// <para>
    /// The two tests above run both subjects as <c>system_admin</c>, so they cannot tell an
    /// attribution swap from an authority swap — a shim that wrongly restored the requester's
    /// <b>permission set</b> as well would pass them both. Here the requester is a
    /// <c>paralegal</c>, who holds <c>tools.legal.add_task</c> and emphatically not
    /// <c>approvals.manage</c> (that gap is #76), and the approver is <c>system_admin</c>. The
    /// paralegal parks a write they are entitled to park; the admin releases it; the record names
    /// the paralegal. If the shim ever starts substituting permissions rather than identity, the
    /// permission assertion at the end is what notices.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_write_parked_by_a_paralegal_is_recorded_against_them_when_an_admin_releases_it()
    {
        using var paralegal = Client("wall-paralegal", "Docketing Paralegal", roles: "paralegal");
        using var admin = Client("wall-partner", "Releasing Partner");

        var paralegalName = await DisplayNameAsync(paralegal);
        var adminName = await DisplayNameAsync(admin);
        Assert.NotEqual(paralegalName, adminName);

        var paralegalId = await UserIdAsync(paralegal);
        var adminId = await UserIdAsync(admin);
        Assert.NotEqual(paralegalId, adminId);

        // The paralegal baseline does not grant matter creation, so the matter is seeded by the
        // admin — which is also the realistic shape: the firm opens the matter, the paralegal works
        // it. It carries no ethical wall, so `legal.matters.view` is enough to reach it.
        const string matter = "Docket Co - Litigation";
        await SeedMatterAsync(admin, matter);

        // A paralegal cannot read /api/chat/approvals at all (no approvals.manage — #76), so the
        // queue is observed through the admin. That is the point: the two subjects genuinely differ
        // in authority, not just in name.
        var before = await PendingIdsAsync(admin);
        await StreamTurnAsync(paralegal, $"add_task on '{matter}' titled 'Serve the notice'");
        var id = Assert.Single((await PendingIdsAsync(admin)).Except(before));

        var approved = await admin.PostAsJsonAsync($"/api/chat/approvals/{id}/approve", new { });
        approved.EnsureSuccessStatusCode();
        Assert.Equal(
            "Executed",
            (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using (scope)
        {
            var task = await scope.ServiceProvider.GetRequiredService<LegalDbContext>()
                .MatterTasks.AsNoTracking().SingleAsync(t => t.Title == "Serve the notice");

            // The tool body's own attribution field, which for a module-owned entity is the whole
            // record of who acted: LegalDbContext has no audit interceptor at all (#70), so
            // IAuditable.CreatedBy is null here rather than naming anyone. Asserted explicitly so
            // that when #70 lands and starts stamping it, this test is the thing that notices —
            // and so nobody reads the absence as this shim failing to attribute the write.
            Assert.Equal(paralegalId, task.CreatedByUserId);
            Assert.NotEqual(adminId, task.CreatedByUserId);
            Assert.Null(task.CreatedBy);
        }
    }

    private HttpClient Client(string subject, string displayName, string roles = "system_admin")
    {
        var client = fixture.AdminClient(roles: roles, subject: subject);
        client.DefaultRequestHeaders.Add("X-Dev-Name", displayName);
        return client;
    }

    private static async Task<Guid> UserIdAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/platform/me")).GetProperty("userId").GetGuid();

    private static async Task<string?> DisplayNameAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/platform/me"))
        .GetProperty("displayName").GetString();

    private static async Task<HashSet<Guid>> PendingIdsAsync(HttpClient client)
    {
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        return [.. pending.EnumerateArray().Select(a => a.GetProperty("id").GetGuid())];
    }

    private static async Task SeedMatterAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/legal/matters", new
        {
            name,
            clientName = "Attribution Co",
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
