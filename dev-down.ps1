<#
.SYNOPSIS
    OASIS Sleek -- full-stack dev teardown (PowerShell).

.PARAMETER Wipe
    Also drop the surrealdb_data volume so the next dev-up sees a fresh DB.

.EXAMPLE
    ./dev-down.ps1
    ./dev-down.ps1 -Wipe
#>
[CmdletBinding()]
param([switch]$Wipe)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ComposeFile = Join-Path $ScriptDir "docker-compose.dev.yml"

# Same compose-runtime detection as dev-up.ps1 -- returns @{ Exe; PreArgs }
# so the call site can splat cleanly regardless of one-word vs two-word
# runtimes.
function Find-Compose {
    try {
        $null = docker compose version 2>&1
        if ($LASTEXITCODE -eq 0) { return @{ Exe = 'docker'; PreArgs = @('compose') } }
    } catch { }
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        return @{ Exe = 'docker-compose'; PreArgs = @() }
    }
    if (Get-Command podman-compose -ErrorAction SilentlyContinue) {
        return @{ Exe = 'podman-compose'; PreArgs = @() }
    }
    try {
        $null = podman compose version 2>&1
        if ($LASTEXITCODE -eq 0) { return @{ Exe = 'podman'; PreArgs = @('compose') } }
    } catch { }
    return $null
}

$compose = Find-Compose
if ($null -eq $compose) {
    Write-Error "[dev-down] FATAL: no compose runtime found."
    exit 1
}

function Invoke-Compose {
    param([string[]]$Arguments)
    $full = @()
    $full += $compose.PreArgs
    $full += $Arguments
    & $compose.Exe @full
}

# Tolerate the missing-surrealdb-container error that podman-compose
# emits when dev-up.ps1 detected an external SurrealDB and skipped the
# bundled service. The API + frontend containers still get removed
# correctly; podman-compose just exits non-zero because it tried to
# remove a container that was never created.
$prevPref = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

if ($Wipe) {
    Write-Host "[dev-down] Tearing down with -v (wipes surrealdb_data volume) ..."
    Invoke-Compose @('-f', $ComposeFile, 'down', '-v', '--remove-orphans')
} else {
    Write-Host "[dev-down] Tearing down (volume preserved) ..."
    Invoke-Compose @('-f', $ComposeFile, 'down', '--remove-orphans')
}

$ErrorActionPreference = $prevPref
exit 0
