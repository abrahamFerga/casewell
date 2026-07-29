using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// The platform seams a change can silently break: the module has to load, its tools have to be
/// registered behind the right permission, and the liveness probes have to answer outside
/// Development. Each of these compiles perfectly when broken.
/// </summary>
[Collection("api")]
public sealed class PlatformSurfaceTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Alive_and_health_both_answer()
    {
        using var client = fixture.AdminClient();

        // /health has been Development-only by accident before, so production probes 404 against
        // a perfectly healthy app. Assert both.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Legal_module_loads_with_its_tabs()
    {
        using var client = fixture.AdminClient();

        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");

        var legal = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "legal");
        Assert.Equal("Legal", legal.GetProperty("displayName").GetString());

        // A manifest that fails to parse yields a module with no tabs rather than an error.
        var tabs = legal.GetProperty("tabs").EnumerateArray()
            .Select(t => t.GetProperty("id").GetString())
            .ToList();
        Assert.Contains("matters", tabs);
        Assert.Contains("time", tabs);
    }

    [Fact]
    public async Task Dev_auth_resolves_a_tenant_and_the_wildcard_permission()
    {
        using var client = fixture.AdminClient();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");

        Assert.NotEqual(Guid.Empty, me.GetProperty("tenantId").GetGuid());
        Assert.NotEqual(Guid.Empty, me.GetProperty("userId").GetGuid());
        Assert.Contains("*", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Every_legal_tool_is_registered_behind_a_matching_permission()
    {
        using var client = fixture.AdminClient();

        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/admin/security/catalog");
        var legal = catalog.GetProperty("modules").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "legal");
        var tools = legal.GetProperty("tools").EnumerateArray().ToList();

        // A tool declared in ModuleManifest.Tools but missing from IModuleToolSource compiles,
        // never errors, and is never callable. The catalog is where that gap shows.
        Assert.Contains(tools, t => t.GetProperty("permission").GetString() == "tools.legal.list_matters");
        Assert.Contains(tools, t => t.GetProperty("permission").GetString() == "tools.legal.log_time");

        // Every tool permission follows Permissions.ForTool(moduleId, name); a hand-written string
        // that disagrees with the manifest 403s for even system_admin.
        Assert.All(tools, t =>
            Assert.StartsWith("tools.legal.", t.GetProperty("permission").GetString()));

        // Writes must be gated. These are approval-gated by construction; if one flips to false
        // the human-in-the-loop gate silently disappears.
        foreach (var gated in new[] { "create_matter", "record_trust_deposit", "record_trust_disbursement" })
        {
            var tool = tools.Single(t => t.GetProperty("permission").GetString() == $"tools.legal.{gated}");
            Assert.True(tool.GetProperty("requiresApproval").GetBoolean(), $"{gated} must require approval");
        }
    }
}
