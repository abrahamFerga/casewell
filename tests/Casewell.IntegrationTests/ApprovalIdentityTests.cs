using System.Net.Http.Json;
using System.Text.Json;
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

    private HttpClient Client(string subject, string displayName)
    {
        var client = fixture.AdminClient(subject: subject);
        client.DefaultRequestHeaders.Add("X-Dev-Name", displayName);
        return client;
    }

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
