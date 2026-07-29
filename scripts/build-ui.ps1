# Builds the Casewell web UI from the Cortex frontend packages and embeds it into the host.
#
# The @cortex/* packages are not on npm (and don't need to be): the API host serves the built
# SPA itself — @cortex/ui at "/" (wwwroot/app) and @cortex/admin-ui at "/admin" (wwwroot/admin),
# same origin as the API, no CORS, no registry. This script bakes Casewell's branding and a
# same-origin API base into the bundles, then copies them into src/Casewell.Host/wwwroot.
# The outputs are COMMITTED (like .packages/) so a clone runs without a Cortex checkout;
# re-run this script after pulling Cortex frontend changes.
#
# Usage:  ./scripts/build-ui.ps1 [-CortexRepo <path>]   (default: the sibling ../Cortex checkout)

param(
    [string]$CortexRepo = (Join-Path $PSScriptRoot "..\..\Cortex")
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$CortexRepo = Resolve-Path $CortexRepo
$frontend = Join-Path $CortexRepo "frontend"

if (-not (Test-Path (Join-Path $frontend "cortex-ui\package.json"))) {
    throw "No Cortex frontend at '$frontend' — pass -CortexRepo pointing at a Cortex checkout."
}

Write-Host "Installing frontend workspace deps..." -ForegroundColor Cyan
pnpm -C $frontend install
if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }

# Casewell branding + same-origin API base ("" -> relative /api/... calls; the host serves both).
$env:VITE_BRAND_NAME = "Casewell"
$env:VITE_API_BASE = ""
try {
    Write-Host "Building @cortex/ui (branded 'Casewell', same-origin API)..." -ForegroundColor Cyan
    pnpm -C (Join-Path $frontend "cortex-ui") build:app
    if ($LASTEXITCODE -ne 0) { throw "@cortex/ui app build failed" }

    Write-Host "Building @cortex/admin-ui (same-origin API)..." -ForegroundColor Cyan
    pnpm -C (Join-Path $frontend "admin-ui") build
    if ($LASTEXITCODE -ne 0) { throw "@cortex/admin-ui build failed" }
}
finally {
    Remove-Item Env:VITE_BRAND_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:VITE_API_BASE -ErrorAction SilentlyContinue
}

$appTarget = Join-Path $repoRoot "src\Casewell.Host\wwwroot\app"
$adminTarget = Join-Path $repoRoot "src\Casewell.Host\wwwroot\admin"

foreach ($pair in @(
    @{ Source = Join-Path $frontend "cortex-ui\dist-app"; Target = $appTarget; Name = "domain UI" },
    @{ Source = Join-Path $frontend "admin-ui\dist"; Target = $adminTarget; Name = "admin console" }
)) {
    if (Test-Path $pair.Target) { Remove-Item -Recurse -Force $pair.Target }
    New-Item -ItemType Directory -Force (Split-Path $pair.Target) | Out-Null
    Copy-Item -Recurse $pair.Source $pair.Target
    $count = (Get-ChildItem -Recurse -File $pair.Target).Count
    Write-Host "Embedded $($pair.Name): $count file(s) -> $($pair.Target)" -ForegroundColor Green
}

Write-Host "`nDone. Run the host and open it directly - the API now serves the Casewell UI at / and /admin." -ForegroundColor Green
