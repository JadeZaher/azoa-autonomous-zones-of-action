<#
.SYNOPSIS
    Stop (and optionally remove) the oasis-surrealdb test container.

.DESCRIPTION
    Brings down the compose stack started by start-test-container.ps1.
    By default, the named volume is preserved (persistent pattern) so that
    data survives between test runs. Pass -RemoveVolumes to wipe completely
    (useful for a clean-slate reset in CI or local debugging).

.PARAMETER ComposeFile
    Path to the compose file. Defaults to docker-compose.surrealdb.yml in
    the repository root (relative to this script's location).

.PARAMETER RemoveVolumes
    If specified, passes --volumes to docker compose down, deleting the
    surrealdb_data named volume. USE WITH CAUTION — all persisted data is lost.

.EXAMPLE
    pwsh scripts/surrealdb/stop-test-container.ps1
    pwsh scripts/surrealdb/stop-test-container.ps1 -RemoveVolumes
#>
[CmdletBinding()]
param(
    [string]$ComposeFile = "",
    [switch]$RemoveVolumes
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

# ── Detect runtime ────────────────────────────────────────────────────────────

function Find-ComposeCmd {
    try {
        $null = docker compose version 2>&1
        if ($LASTEXITCODE -eq 0) { return @('docker', 'compose') }
    } catch { }
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) { return @('docker-compose') }
    if (Get-Command podman-compose -ErrorAction SilentlyContinue) { return @('podman-compose') }
    Write-Error "No container compose runtime found."
    exit 1
}

$composeCmd = Find-ComposeCmd

# ── Bring down ────────────────────────────────────────────────────────────────

$downArgs = @('-f', $ComposeFile, 'down')
if ($RemoveVolumes) {
    $downArgs += '--volumes'
    Write-Warning "Removing named volume surrealdb_data — all persisted data will be lost."
}

Write-Host "Stopping oasis-surrealdb..."
& $composeCmd[0] ($composeCmd[1..($composeCmd.Length - 1)] + $downArgs)

if ($LASTEXITCODE -ne 0) {
    Write-Error "Compose down failed (exit $LASTEXITCODE)."
    exit 1
}

Write-Host "oasis-surrealdb stopped."
exit 0
