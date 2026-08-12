using System.Net.Http.Json;
using Cortex.Modules.Legal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// The conflict search surface (issue #63). A conflict check exists to prevent a false negative,
/// and the ABA Rule 1.9 (former client) / 1.18 (prospective client) case is exactly a name that is
/// in the firm's contact book but not yet attached to any matter. Asserting on the tool's own
/// output rather than on a query means the test fails if the Clients book drops out of scope again.
/// </summary>
[Collection("api")]
public sealed class ConflictSearchTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task A_client_in_the_book_with_no_matter_is_a_conflict_hit()
    {
        const string name = "Globex Book Only";
        using var client = fixture.AdminClient();

        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using (scope)
        {
            var tools = scope.ServiceProvider.GetRequiredService<ConflictTools>();

            // Baseline: nothing of that name exists anywhere yet.
            var before = await tools.CheckConflicts(name);
            Assert.StartsWith("No conflicts found", before);

            // Enter it in the contact book only — no matter, no party row.
            var created = await client.PostAsJsonAsync("/api/legal/clients", new
            {
                name,
                organization = "Globex Industries",
            });
            created.EnsureSuccessStatusCode();

            // The defect: this returned "No conflicts found" because only matter parties and
            // Matter.ClientName were searched.
            var after = await tools.CheckConflicts(name);
            Assert.StartsWith("POTENTIAL CONFLICTS", after);
            Assert.Contains(name, after);
            Assert.Contains("client book", after);
        }
    }

    [Fact]
    public async Task A_client_already_on_a_matter_is_reported_once_from_the_matter()
    {
        const string name = "Initech Booked";
        using var client = fixture.AdminClient();

        var created = await client.PostAsJsonAsync("/api/legal/clients", new { name });
        created.EnsureSuccessStatusCode();
        var matter = await client.PostAsJsonAsync("/api/legal/matters", new
        {
            name = "Initech Booked - Retainer",
            clientName = name,
        });
        matter.EnsureSuccessStatusCode();

        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using (scope)
        {
            var tools = scope.ServiceProvider.GetRequiredService<ConflictTools>();
            var result = await tools.CheckConflicts(name);

            // The matter hit is strictly more informative than the book hit, so the book row must
            // not double-report the same name.
            Assert.Contains("POTENTIAL CONFLICTS — 1 hit(s)", result);
            Assert.Contains("Initech Booked - Retainer", result);
            Assert.DoesNotContain("client book", result);
        }
    }
}
