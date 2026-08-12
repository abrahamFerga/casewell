using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// Issue #75: the ethical wall has to hold on <b>every</b> surface that counts or names matters,
/// not only on the ones that list them. A wall restricts by identity rather than by permission,
/// so these assertions run over HTTP through <c>AdminClient</c> with a wildcard role deliberately
/// — a <c>system_admin</c> outside the wall must still be excluded, and a scope-based test could
/// not prove it because <c>AuthorizedScopeAsync</c> grants <c>*</c> and skips the pipeline.
/// </summary>
[Collection("api")]
public sealed class EthicalWallTests(IntegrationFixture fixture)
{
    private const string ClientName = "Wall Leak Ltd";
    private const string MatterName = "Wall Leak Ltd - Confidential Acquisition";

    [Fact]
    public async Task Clients_tab_matter_count_excludes_a_matter_the_caller_is_walled_out_of()
    {
        // Two genuinely different users: the subject is what provisions them, and the same subject
        // resolves to the same user id on the HTTP path below.
        var insiderId = await ProvisionAsync("wall-insider");
        var outsiderId = await ProvisionAsync("wall-outsider");
        Assert.NotEqual(insiderId, outsiderId);

        using (var admin = fixture.AdminClient())
        {
            (await admin.PostAsJsonAsync("/api/legal/matters", new
            {
                name = MatterName, clientName = ClientName, practiceArea = "Corporate",
            })).EnsureSuccessStatusCode();

            // The contact book is a separate record from the engagement — the leak is that the two
            // disagree, so both halves have to exist for the test to mean anything.
            (await admin.PostAsJsonAsync("/api/legal/clients", new { name = ClientName }))
                .EnsureSuccessStatusCode();
        }

        // Wall the matter to the insider. Written exactly as MatterTools.RestrictMatterAccess
        // writes it, so this is the production shape and not a test-only encoding.
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
            var matter = await db.Matters.SingleAsync(m => m.Name == MatterName);
            matter.RestrictedUserIdsJson = JsonSerializer.Serialize(new[] { insiderId });
            await db.SaveChangesAsync();
        }

        using var outsider = fixture.AdminClient(subject: "wall-outsider");

        // The Matters tab already holds the line — this is the control. If it ever stops holding,
        // the assertion below would pass for the wrong reason.
        var matters = await outsider.GetFromJsonAsync<JsonElement>("/api/legal/matters");
        Assert.DoesNotContain(
            matters.EnumerateArray(),
            m => m.GetProperty("name").GetString() == MatterName);

        // ...so the Clients tab may not answer "1 matter" for the same client to the same caller.
        // A count is the fact the wall exists to withhold: it tells an outsider the firm acts for
        // this client, which is the whole disclosure.
        Assert.Equal(0, await MatterCountAsync(outsider, ClientName));

        // And the wall redacts rather than blanks: inside it, the count is unchanged.
        using var insider = fixture.AdminClient(subject: "wall-insider");
        Assert.Contains(
            (await insider.GetFromJsonAsync<JsonElement>("/api/legal/matters")).EnumerateArray(),
            m => m.GetProperty("name").GetString() == MatterName);
        Assert.Equal(1, await MatterCountAsync(insider, ClientName));
    }

    private async Task<Guid> ProvisionAsync(string subject)
    {
        var (scope, _, userId) = await fixture.AuthorizedScopeAsync(subject: subject);
        scope.Dispose();
        return userId;
    }

    /// <summary>The <c>matters</c> field of one client row on the Clients tab's own dataEndpoint.</summary>
    private static async Task<int> MatterCountAsync(HttpClient client, string name)
    {
        var clients = await client.GetFromJsonAsync<JsonElement>("/api/legal/clients");
        var row = clients.EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        return row.GetProperty("matters").GetInt32();
    }
}
