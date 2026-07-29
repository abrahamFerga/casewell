// Casewell local orchestration — the full Cortex-based stack in one command:
//   dotnet run --project src/Casewell.AppHost    (or `aspire run`)
//
// Zero-config: the chat assistant uses Cortex's dependency-free Mock provider and the RAG
// pipeline uses the deterministic Mock embedder, so no API keys are required. The web UI ships
// from the Cortex frontend packages (@cortex/ui, @cortex/admin-ui); until they publish to npm,
// the AppHost launches them as Vite dev servers straight from a sibling Cortex checkout
// (default ../Cortex next to this repo; override with "CortexRepoPath" in appsettings or
// user-secrets). No checkout found → the API still runs, the UI resources are skipped.

var builder = DistributedApplication.CreateBuilder(args);

// STABLE dev password (overridable via Parameters:cortex-pg-password in user-secrets). Postgres
// bakes the password into the data volume at first init and never re-reads it — with Aspire's
// default *generated* password, losing/regenerating user-secrets leaves the volume unopenable
// ("28P01 password authentication failed", the API waits forever, the console shows nothing).
// A fixed dev default can't drift. Local demo container only — not a production credential.
var pgPassword = builder.AddParameter("cortex-pg-password", "casewell-dev-only", secret: true);

var postgres = builder.AddPostgres("cortex-pg", password: pgPassword)
    // pgvector-enabled Postgres — Cortex's opt-in RAG pipeline needs the vector extension at
    // migration time. pg17 pairs with Aspire's data-volume mount (see the Cortex AppHost notes:
    // a volume created by a different Postgres major needs `docker volume rm` to reset).
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume();

var platformDb = postgres.AddDatabase("cortex-platform");
var auditDb = postgres.AddDatabase("cortex-audit");

var redis = builder.AddRedis("cortex-redis");

// AI + embedding providers stay parameters so a real model is a user-secret away:
//   dotnet user-secrets --project src/Casewell.AppHost set "Parameters:ai-provider" "OpenAI"
//   dotnet user-secrets --project src/Casewell.AppHost set "Parameters:ai-api-key"  "sk-..."
var aiProvider = builder.AddParameter("ai-provider", "Mock", publishValueAsDefault: true);
var aiModel = builder.AddParameter("ai-model", "gpt-4o-mini", publishValueAsDefault: true);
var aiApiKey = builder.AddParameter("ai-api-key", "", secret: true);

var api = builder.AddProject<Projects.Casewell_Host>("casewell-api")
    .WithReference(platformDb)
    .WithReference(auditDb)
    .WithReference(redis)
    .WaitFor(platformDb)
    .WaitFor(auditDb)
    .WithEnvironment("Ai__Provider", aiProvider)
    .WithEnvironment("Ai__Model", aiModel)
    .WithEnvironment("Ai__ApiKey", aiApiKey)
    .WithExternalHttpEndpoints();

// ── Front-ends (Vite dev servers from the Cortex checkout, until @cortex/* publish to npm) ──
var cortexRepo = Path.GetFullPath(
    builder.Configuration["CortexRepoPath"] ?? Path.Combine(builder.AppHostDirectory, "..", "..", "..", "Cortex"));
var workspaceDir = Path.Combine(cortexRepo, "frontend", "cortex-ui");
var adminDir = Path.Combine(cortexRepo, "frontend", "admin-ui");

if (builder.ExecutionContext.IsRunMode && Directory.Exists(workspaceDir) && ToolExistsOnPath("pnpm"))
{
    var workspace = builder.AddViteApp("casewell-ui", workspaceDir)
        .WithPnpm()
        .WaitFor(api)
        .WithEnvironment("VITE_BRAND_NAME", "Casewell")
        .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
        .WithExternalHttpEndpoints();

    var admin = builder.AddViteApp("casewell-admin-ui", adminDir)
        .WithPnpm()
        .WaitFor(api)
        .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
        .WithEnvironment("VITE_WORKSPACE_URL", workspace.GetEndpoint("http"))
        .WithExternalHttpEndpoints();

    // The workspace's "Admin" link targets the admin console (Vite serves it under its /admin base).
    workspace.WithEnvironment(
        "VITE_ADMIN_URL",
        ReferenceExpression.Create($"{admin.GetEndpoint("http")}/admin"));

    // Teach the API's CORS policy the front-end origins (ports are assigned dynamically); the
    // fixed localhost ports cover running `pnpm dev` outside Aspire.
    api.WithEnvironment("Cors__Origins__0", workspace.GetEndpoint("http"))
       .WithEnvironment("Cors__Origins__1", admin.GetEndpoint("http"))
       .WithEnvironment("Cors__Origins__2", "http://localhost:5173")
       .WithEnvironment("Cors__Origins__3", "http://localhost:5174");
}
else if (builder.ExecutionContext.IsRunMode)
{
    Console.WriteLine(
        $"[Casewell.AppHost] UI resources skipped — Cortex checkout not found at '{cortexRepo}' " +
        "(set \"CortexRepoPath\") or pnpm is not on PATH (`corepack enable`). The API runs without them.");
}

builder.Build().Run();

// True when `tool` resolves on PATH (Windows launchers included — pnpm installs as pnpm.cmd).
static bool ToolExistsOnPath(string tool)
{
    var extensions = OperatingSystem.IsWindows() ? new[] { ".cmd", ".exe", ".bat", "" } : new[] { "" };
    return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(_ => extensions, (dir, ext) => Path.Combine(dir.Trim('"'), tool + ext))
        .Any(File.Exists);
}
