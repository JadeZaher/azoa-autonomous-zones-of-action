<#
.SYNOPSIS
    surreal-linq-graph-query -- focused build + test loop for the SurrealDB
    LINQ / graph / live-query package work (PowerShell).

.DESCRIPTION
    Iterate on the typed query layer (Oasis.SurrealDb.Client / .Schema /
    .Analyzer) WITHOUT spinning the full docker dev stack. Builds the three
    packages, runs their unit-test projects, and optionally brings up a
    SurrealDB so the live-query (LIVE SELECT over WebSocket) and graph
    integration tests can run end-to-end.

    Track:  conductor/tracks/surreal-linq-graph-query/{spec,plan}.md

    Phase map (see plan.md):
      P1 translator broaden | P2 IQueryable | P3 SurrealContext
      P4 graph traversal    | P5 live socket (ExecuteLiveAsync)

.PARAMETER Filter
    xUnit filter passed to `dotnet test --filter`. e.g.
        ./launch-surreal-linq.ps1 -Filter "FullyQualifiedName~Graph"
        ./launch-surreal-linq.ps1 -Filter "FullyQualifiedName~Live"
        ./launch-surreal-linq.ps1 -Filter "FullyQualifiedName~ExpressionTranslator"

.PARAMETER Live
    Bring up a throwaway SurrealDB (port 8000, root/root) before testing so
    the WebSocket live-query + graph integration tests can connect, then tear
    it down afterward. Requires docker or podman. Without this, integration
    tests that need a live DB self-skip (SkippableFact).

.PARAMETER NoBuild
    Skip the package build; run tests against the last build. Fast inner loop.

.PARAMETER Watch
    `dotnet watch test` on the client test project -- re-run on file change.
    The tightest TDD loop for Phase 1-4 translator/provider work.

.PARAMETER IntegrationOnly
    Run ONLY the integration test project (Oasis.SurrealDb.Client.IntegrationTests).
    Implies -Live unless a SurrealDB is already reachable on :8000.

.EXAMPLE
    ./launch-surreal-linq.ps1                          # build + all package unit tests
    ./launch-surreal-linq.ps1 -Filter "~Graph"         # just the graph tests
    ./launch-surreal-linq.ps1 -Live -IntegrationOnly   # spin DB, run live/graph integration
    ./launch-surreal-linq.ps1 -Watch                   # TDD watch loop on the client tests
#>
[CmdletBinding()]
param(
    [string]$Filter,
    [switch]$Live,
    [switch]$NoBuild,
    [switch]$Watch,
    [switch]$IntegrationOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $ScriptDir

# Package projects (the surface this track extends).
$Packages = @(
    'packages/Oasis.SurrealDb.Client/Oasis.SurrealDb.Client.csproj',
    'packages/Oasis.SurrealDb.Schema/Oasis.SurrealDb.Schema.csproj',
    'packages/Oasis.SurrealDb.Analyzer/Oasis.SurrealDb.Analyzer.csproj'
)

# Unit-test projects exercised on every run (fast, no DB needed).
$UnitTestProjects = @(
    'tests/Oasis.SurrealDb.Client.Tests/Oasis.SurrealDb.Client.Tests.csproj',
    'tests/Oasis.SurrealDb.Schema.Tests/Oasis.SurrealDb.Schema.Tests.csproj',
    'tests/Oasis.SurrealDb.Analyzer.Tests/Oasis.SurrealDb.Analyzer.Tests.csproj'
)

# Integration project (needs a live SurrealDB for graph/live coverage).
$IntegrationProject = 'tests/Oasis.SurrealDb.Client.IntegrationTests/Oasis.SurrealDb.Client.IntegrationTests.csproj'

$ClientTestProject = 'tests/Oasis.SurrealDb.Client.Tests/Oasis.SurrealDb.Client.Tests.csproj'

function Find-ContainerRuntime {
    foreach ($rt in @('docker', 'podman')) {
        if (Get-Command $rt -ErrorAction SilentlyContinue) { return $rt }
    }
    return $null
}

function Test-SurrealUp {
    try {
        $null = Invoke-WebRequest -Uri 'http://127.0.0.1:8000/health' -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        return $true
    } catch { return $false }
}

$SurrealContainer = 'surreal-linq-devdb'
$StartedDb = $false

function Start-Surreal {
    if (Test-SurrealUp) {
        Write-Host '[launch] SurrealDB already reachable on :8000 -- reusing it.'
        return
    }
    $rt = Find-ContainerRuntime
    if ($null -eq $rt) {
        Write-Error '[launch] -Live/-IntegrationOnly needs SurrealDB but no docker/podman on PATH. Install one, or start SurrealDB on :8000 yourself.'
        exit 1
    }
    Write-Host "[launch] Starting throwaway SurrealDB ($rt, in-memory, root/root) on :8000 ..."
    # In-memory engine: no volume, nothing to clean up but the container.
    & $rt run -d --rm --name $SurrealContainer -p 8000:8000 `
        surrealdb/surrealdb:latest start --user root --pass root memory | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error '[launch] Failed to start SurrealDB container.'; exit 1 }
    $script:StartedDb = $true

    for ($i = 0; $i -lt 30; $i++) {
        if (Test-SurrealUp) { Write-Host '[launch] SurrealDB is up.'; return }
        Start-Sleep -Milliseconds 400
    }
    Write-Error '[launch] SurrealDB container started but never became healthy on :8000.'
    & $rt logs $SurrealContainer 2>&1 | Write-Host
    exit 1
}

function Stop-Surreal {
    if (-not $script:StartedDb) { return }
    $rt = Find-ContainerRuntime
    if ($null -ne $rt) {
        Write-Host '[launch] Tearing down throwaway SurrealDB ...'
        & $rt rm -f $SurrealContainer 2>&1 | Out-Null
    }
}

try {
    # ── Build ──────────────────────────────────────────────────────────────
    if (-not $NoBuild) {
        Write-Host '[launch] Building SurrealDB packages (zero-new-warnings gate) ...'
        foreach ($p in $Packages) {
            Write-Host "  build: $p"
            dotnet build $p -c Debug --nologo
            if ($LASTEXITCODE -ne 0) { Write-Error "[launch] Build failed: $p"; exit 1 }
        }
    } else {
        Write-Host '[launch] -NoBuild: skipping package build.'
    }

    # ── Watch loop (TDD inner loop, mutually exclusive with one-shot test) ──
    if ($Watch) {
        Write-Host "[launch] dotnet watch test on $ClientTestProject (Ctrl-C to stop) ..."
        $watchArgs = @('watch', 'test', '--project', $ClientTestProject)
        if ($Filter) { $watchArgs += @('--filter', $Filter) }
        & dotnet @watchArgs
        return
    }

    # ── DB (only when graph/live integration coverage is requested) ────────
    if ($Live -or $IntegrationOnly) { Start-Surreal }

    # ── Test ───────────────────────────────────────────────────────────────
    $testTargets = if ($IntegrationOnly) { @($IntegrationProject) }
                   elseif ($Live)        { $UnitTestProjects + @($IntegrationProject) }
                   else                  { $UnitTestProjects }

    $failed = @()
    foreach ($t in $testTargets) {
        Write-Host ''
        Write-Host "[launch] test: $t"
        $testArgs = @('test', $t, '--nologo')
        if ($NoBuild) { $testArgs += '--no-build' }
        if ($Filter)  { $testArgs += @('--filter', $Filter) }
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { $failed += $t }
    }

    Write-Host ''
    if ($failed.Count -gt 0) {
        Write-Host '[launch] FAILED projects:' -ForegroundColor Red
        $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host '[launch] All targeted tests green.' -ForegroundColor Green
}
finally {
    Stop-Surreal
    Pop-Location
}
