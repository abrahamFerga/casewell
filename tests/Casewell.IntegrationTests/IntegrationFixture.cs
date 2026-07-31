using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    public HttpClient AdminClient(string roles = "system_admin", string subject = "it-admin")
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", roles);
        return client;
    }

    private sealed class CasewellAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<IntegrationFixture>;
