using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// The real Casewell host on a throwaway Postgres: platform + legal migrations run, the dev tenant
/// and seed data land, hosted services start. Everything is real except the AI provider (Mock) —
/// the keyless posture that makes the whole security pipeline testable on a cold clone and in CI.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        // Ryuk (the resource reaper) is flaky against Docker Desktop on Windows; containers are
        // disposed explicitly below.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");

        // pgvector, not stock postgres: the platform's RAG migration creates a vector column and
        // fails at startup on an image without the extension. Same tag the AppHost pins.
        _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("cortex_platform")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        var connectionString = _postgres.GetConnectionString();

        // The Aspire AppHost supplies these via WithReference; nothing is in appsettings, so a
        // test host must provide them itself. Platform and audit share one database here.
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-platform", connectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-audit", connectionString);

        // appsettings.json pins Ai:Provider to "None" and there is no appsettings.Development.json,
        // so without this every chat turn answers RUN_ERROR "AI provider is not configured".
        // The AppHost does the same thing via the ai-provider parameter.
        Environment.SetEnvironmentVariable("Ai__Provider", "Mock");

        Factory = new CasewellAppFactory();

        // The first request boots the host (migrations + seeding); the authenticated call makes the
        // request enricher provision the dev tenant and its user.
        using var warmup = AdminClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();
        (await warmup.GetAsync("/api/platform/modules")).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-platform", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__cortex-audit", null);
        Environment.SetEnvironmentVariable("Ai__Provider", null);

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// An authorized HTTP client for the dev tenant. PREFER THIS: it goes through the real
    /// pipeline, so it is the only way to prove RBAC, the approval gate, and the AG-UI protocol.
    /// Pass a narrower role to assert a 403.
    /// </summary>
    public HttpClient AdminClient(string roles = "system_admin", string subject = "it-admin", string tenant = "dev")
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", roles);
        return client;
    }

    /// <summary>
    /// Creates a SECOND real tenant, so a test can prove tenant isolation rather than assert it.
    /// Idempotent.
    ///
    /// This exists because a cross-tenant probe that merely invents an <c>X-Dev-Tenant</c> value
    /// proves nothing: <c>RequestEnricher.ResolveTenantAsync</c> looks the slug up in
    /// <c>Tenants</c> and returns null when it misses, so the enricher returns before
    /// <c>SetTenant</c>/<c>SetPermissions</c> and the caller is refused at RBAC with 403 —
    /// the query filter under test is never reached. Only a tenant that actually exists gets far
    /// enough to exercise it.
    ///
    /// Mirrors <c>DatabaseInitializer.SeedDevTenantAsync</c> at the pinned
    /// <c>Cortex 0.1.0-alpha.14</c>: insert the tenant, then enable the same modules. Two details
    /// are load-bearing and both are the platform's, verified against source at that tag.
    /// (1) <c>IgnoreQueryFilters()</c> on every read — <c>TenantModule</c> is tenant-owned and this
    /// scope has no ambient tenant, so the default filter would hide existing rows and the
    /// idempotence check would re-insert on a second call. (2) No <c>RolePermission</c> rows are
    /// copied: <c>PermissionResolver</c> falls back to the built-in role baseline for a tenant that
    /// has none, so the new tenant's <c>system_admin</c> resolves identically to the dev tenant's
    /// without this fixture seeding a single grant of its own.
    /// </summary>
    public async Task<Guid> EnsureTenantAsync(string slug, string name)
    {
        using var scope = Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // Tenants are not tenant-owned, so no query filter applies to this read.
        var tenant = await platform.Tenants.FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
        {
            tenant = new Tenant { Name = name, Slug = slug };
            platform.Tenants.Add(tenant);
            await platform.SaveChangesAsync();
        }

        // Copy the dev tenant's enabled module set rather than resolving IModule here, so this
        // fixture cannot drift from what the host actually installed.
        var devTenantId = await platform.Tenants
            .Where(t => t.Slug == "dev")
            .Select(t => t.Id)
            .FirstAsync();

        var moduleIds = await platform.TenantModules.IgnoreQueryFilters()
            .Where(tm => tm.TenantId == devTenantId && tm.IsEnabled)
            .Select(tm => tm.ModuleId)
            .ToListAsync();

        foreach (var moduleId in moduleIds)
        {
            if (!await platform.TenantModules.IgnoreQueryFilters()
                    .AnyAsync(tm => tm.TenantId == tenant.Id && tm.ModuleId == moduleId))
            {
                platform.TenantModules.Add(new TenantModule
                {
                    TenantId = tenant.Id,
                    ModuleId = moduleId,
                    IsEnabled = true,
                });
            }
        }

        await platform.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>
    /// A DI scope with tenant + user populated — how module tools run AFTER the platform's
    /// auth/approval pipeline has done its part. Use it for dense domain assertions.
    /// <para>
    /// It deliberately <b>bypasses RBAC and the approval gate</b>, so it can never prove either
    /// works: use <see cref="AdminClient"/> for those. The identity comes from a real
    /// <c>/api/platform/me</c> round trip, so passing a different <paramref name="subject"/>
    /// provisions a genuinely different user — which is what a separation-of-duties test needs.
    /// </para>
    /// </summary>
    public async Task<(IServiceScope Scope, Guid TenantId, Guid UserId)> AuthorizedScopeAsync(
        string subject = "it-admin", string? displayName = null)
    {
        using var client = AdminClient(subject: subject);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var tenantId = me.GetProperty("tenantId").GetGuid();
        var userId = me.GetProperty("userId").GetGuid();

        var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.SetTenant(tenantId);
        context.SetUser(userId, subject, displayName ?? me.GetProperty("displayName").GetString());
        context.SetPermissions(["*"]);
        return (scope, tenantId, userId);
    }

    private sealed class CasewellAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<IntegrationFixture>;
