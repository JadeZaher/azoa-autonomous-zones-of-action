<#
.SYNOPSIS
    Runs the OASIS.WebAPI .NET test suites.

.DESCRIPTION
    Single entry point for the xUnit unit + integration suites. Integration
    tests need SurrealDB on localhost:8000 -- this script brings up the
    `surrealdb` service from docker-compose.dev.yml if it's not already up,
    then runs `dotnet test`. Use -NoDb to skip the bring-up when you know
    SurrealDB is already running (e.g. dev-up handled it).

.PARAMETER Configuration
    Build configuration. Default: Debug.

.PARAMETER Live
    Also run the live HTTP harness (OASIS.WebAPI.LiveTests).

.PARAMETER LiveUrl
    Base URL for the live harness. Default: https://localhost:5001.

.PARAMETER Mutation
    Run Stryker.NET mutation testing instead of the normal suites.

.PARAMETER NoDb
    Skip the SurrealDB bring-up step. Assumes you already have it running.

.EXAMPLE
    ./tests/run-tests.ps1
    ./tests/run-tests.ps1 -Configuration Release
    ./tests/run-tests.ps1 -Live -LiveUrl https://localhost:5001
    ./tests/run-tests.ps1 -Mutation
    ./tests/run-tests.ps1 -NoDb
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [switch]$Live,
    [string]$LiveUrl = "https://localhost:5001",
    [switch]$Mutation,
    [switch]$NoDb
)

$ErrorActionPreference = "Stop"

$TestsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $TestsDir
$ComposeFile = Join-Path $RepoRoot "docker-compose.dev.yml"

function Find-Compose {
    try { $null = docker compose version 2>&1; if ($LASTEXITCODE -eq 0) { return @{ Exe='docker'; Pre=@('compose') } } } catch { }
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) { return @{ Exe='docker-compose'; Pre=@() } }
    if (Get-Command podman-compose -ErrorAction SilentlyContinue) { return @{ Exe='podman-compose'; Pre=@() } }
    try { $null = podman compose version 2>&1; if ($LASTEXITCODE -eq 0) { return @{ Exe='podman'; Pre=@('compose') } } } catch { }
    return $null
}

function Initialize-IntegrationSurrealDb {
    try {
        $null = Invoke-WebRequest -Uri "http://127.0.0.1:8000/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        Write-Host "==> SurrealDB already reachable on localhost:8000." -ForegroundColor DarkGray
        return
    } catch { }

    $compose = Find-Compose
    if (-not $compose) {
        Write-Host "==> No compose runtime; skipping bring-up. Integration tests need SurrealDB on :8000." -ForegroundColor Yellow
        return
    }
    if (-not (Test-Path $ComposeFile)) {
        Write-Host "==> $ComposeFile not found; skipping bring-up." -ForegroundColor Yellow
        return
    }

    Write-Host "==> Bringing up surrealdb from docker-compose.dev.yml..." -ForegroundColor Cyan
    & $compose.Exe @($compose.Pre + @('-f', $ComposeFile, 'up', '-d', 'surrealdb'))
    if ($LASTEXITCODE -ne 0) { throw "compose up surrealdb failed (exit $LASTEXITCODE)." }

    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        try {
            $null = Invoke-WebRequest -Uri "http://127.0.0.1:8000/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
            Write-Host "==> SurrealDB ready on localhost:8000." -ForegroundColor Green
            return
        } catch {
            Start-Sleep -Milliseconds 800
        }
    }
    throw "SurrealDB did not become ready on localhost:8000 within 40s."
}

Push-Location $RepoRoot
try {
    if ($Mutation) {
        Write-Host "==> Stryker.NET mutation testing (output: tests/StrykerOutput)" -ForegroundColor Cyan
        dotnet stryker --output tests/StrykerOutput
        exit $LASTEXITCODE
    }

    if (-not $NoDb) { Initialize-IntegrationSurrealDb }

    $testProjects = @(
        "tests/OASIS.WebAPI.Tests/OASIS.WebAPI.Tests.csproj",
        "tests/OASIS.WebAPI.IntegrationTests/OASIS.WebAPI.IntegrationTests.csproj"
    )

    foreach ($proj in $testProjects) {
        Write-Host "==> dotnet test $proj ($Configuration)" -ForegroundColor Cyan
        dotnet test $proj --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    if ($Live) {
        Write-Host "==> Live HTTP harness against $LiveUrl" -ForegroundColor Cyan
        dotnet run --project tests/OASIS.WebAPI.LiveTests --configuration $Configuration -- --url $LiveUrl
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    Write-Host "==> All requested test suites passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
