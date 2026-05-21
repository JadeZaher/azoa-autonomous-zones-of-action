<#
.SYNOPSIS
    Start the oasis-surrealdb test container (persistent, not ephemeral).
    Mirrors the pattern from [[integration-tests-persistent-postgres]]:
    spin once, reuse across test runs; per-test isolation is done via
    SurrealDB namespace scoping in IntegrationTestBase, NOT via container
    teardown.

.DESCRIPTION
    - Uses docker compose (or podman-compose) with docker-compose.surrealdb.yml.
    - Polls GET /health until SurrealDB is ready (up to 60 s).
    - Exits 0 on success, non-zero on timeout or error.
    - Safe to call repeatedly: if the container is already running, returns 0
      immediately after the health poll succeeds.

.PARAMETER ComposeFile
    Path to the compose file. Defaults to docker-compose.surrealdb.yml in the
    repository root (relative to this script's location).

.PARAMETER TimeoutSeconds
    Maximum seconds to wait for SurrealDB to become healthy. Default: 60.

.PARAMETER Host
    SurrealDB host for the health probe. Default: localhost.

.PARAMETER Port
    SurrealDB port. Default: 8442.

.EXAMPLE
    pwsh scripts/surrealdb/start-test-container.ps1
    pwsh scripts/surrealdb/start-test-container.ps1 -TimeoutSeconds 90
#>
[CmdletBinding()]
param(
    [string]$ComposeFile = "",
    [int]$TimeoutSeconds = 60,
    [string]$SurrealHost = "localhost",
    [int]$Port = 8442
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ─────────────────────────────────────────────────────────────

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)

if (-not $ComposeFile) {
    $ComposeFile = Join-Path $RepoRoot "docker-compose.surrealdb.yml"
}

if (-not (Test-Path $ComposeFile)) {
    Write-Error "Compose file not found: $ComposeFile"
    exit 1
}

# ── Detect runtime (docker compose v2 or podman-compose) ─────────────────────

function Find-ComposeCmd {
    # Prefer docker compose v2 (plugin)
    try {
        $null = docker compose version 2>&1
        if ($LASTEXITCODE -eq 0) { return @('docker', 'compose') }
    } catch { }
    # Fall back to standalone docker-compose v1
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        return @('docker-compose')
    }
    # Fall back to podman-compose
    if (Get-Command podman-compose -ErrorAction SilentlyContinue) {
        return @('podman-compose')
    }
    Write-Error "No container compose runtime found. Install Docker Desktop or Podman."
    exit 1
}

$composeCmd = Find-ComposeCmd

# ── Start the container ───────────────────────────────────────────────────────

Write-Host "Starting oasis-surrealdb via compose ($($composeCmd -join ' '))..."
& $composeCmd[0] ($composeCmd[1..($composeCmd.Length - 1)] + @('-f', $ComposeFile, 'up', '-d', '--remove-orphans'))

if ($LASTEXITCODE -ne 0) {
    Write-Error "Compose up failed (exit $LASTEXITCODE)."
    exit 1
}

# ── Health poll ───────────────────────────────────────────────────────────────

$healthUrl  = "http://${SurrealHost}:${Port}/health"
$deadline   = (Get-Date).AddSeconds($TimeoutSeconds)
$ready      = $false

Write-Host "Polling SurrealDB health at $healthUrl (timeout ${TimeoutSeconds}s)..."

while ((Get-Date) -lt $deadline) {
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {
        # Not ready yet — swallow and retry
    }
    Start-Sleep -Seconds 2
}

if (-not $ready) {
    Write-Error "SurrealDB did not become healthy within ${TimeoutSeconds}s. Check container logs: docker logs oasis-surrealdb"
    exit 1
}

Write-Host "SurrealDB is ready at $healthUrl."
exit 0
