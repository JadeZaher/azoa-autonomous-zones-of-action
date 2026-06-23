<#
.SYNOPSIS
    AZOA Sleek -- full-stack dev teardown (PowerShell).

.PARAMETER Wipe
    Also drop the surrealdb_data volume so the next dev-up sees a fresh DB.

.PARAMETER ResetDb
    Alias for -Wipe. Matches the dev-up.ps1 vocabulary so you can use the
    same flag name across both scripts.

.EXAMPLE
    ./dev-down.ps1            # stop containers, keep volume
    ./dev-down.ps1 -Wipe      # stop containers and drop the SurrealDB volume
    ./dev-down.ps1 -ResetDb   # same as -Wipe
#>
[CmdletBinding()]
param(
    [switch]$Wipe,
    [switch]$ResetDb
)

# Vocabulary alignment with dev-up.ps1 (-ResetDb). Either flag triggers wipe.
if ($ResetDb) { $Wipe = $true }

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

# Detect whether the bundled surrealdb container exists. dev-up.ps1
# skips it when an external SurrealDB is found on localhost:8000, and
# podman-compose then emits "no such container" stderr on `down`
# because it tries to remove a container that was never created. We
# target only the services that actually exist to keep teardown clean.
function Test-Container {
    param([string]$Name)
    $runtime = if ($compose.Exe -like '*podman*') { 'podman' } else { 'docker' }
    if (-not (Get-Command $runtime -ErrorAction SilentlyContinue)) { return $false }
    $ids = & $runtime ps -a --filter "name=^$Name`$" --format '{{.ID}}' 2>$null
    return -not [string]::IsNullOrWhiteSpace(($ids -join ''))
}

$surrealExists = Test-Container 'azoa-dev-surrealdb'
$downArgs = @('-f', $ComposeFile, 'down', '--remove-orphans')
if ($Wipe) { $downArgs += '-v' }

if (-not $surrealExists) {
    Write-Host "[dev-down] bundled SurrealDB container not present -- only tearing down api + frontend."
    $downArgs += @('azoa-api', 'azoa-frontend')
}

# Belt-and-suspenders: suppress non-zero exit from compose `down`. The
# important thing is the containers we asked to remove are gone, which
# we verify implicitly by the fact that dev-up will recreate them.
$prevPref = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

if ($Wipe) {
    Write-Host "[dev-down] Tearing down with -v (wipes surrealdb_data volume) ..."
} else {
    Write-Host "[dev-down] Tearing down (volume preserved -- pass -Wipe / -ResetDb to drop) ..."
}
Invoke-Compose $downArgs

if ($Wipe) {
    # Belt + braces (same pattern as dev-up.ps1): `down -v` only removes
    # volumes declared by THIS compose file. Stray project-labeled volumes
    # from a previous rename or detached anonymous volumes can survive.
    $ProjectName = Split-Path -Leaf $ScriptDir
    $volRuntime = if ($compose.Exe -like '*podman*') { 'podman' } else { 'docker' }
    if (Get-Command $volRuntime -ErrorAction SilentlyContinue) {
        try {
            $stale = & $volRuntime volume ls --filter "label=com.docker.compose.project=$ProjectName" -q 2>$null
            if ($LASTEXITCODE -eq 0 -and $stale) {
                Write-Host "[dev-down] Pruning $($stale.Count) stray project volume(s)..."
                & $volRuntime volume rm -f @stale 2>$null | Out-Null
            }
        } catch {
            # Best-effort -- not all runtimes label volumes consistently.
        }
    }
}

$ErrorActionPreference = $prevPref

Write-Host ""
Write-Host "[dev-down] Flags (run ./dev-down.ps1 -<Flag>):"
Write-Host "  -Wipe / -ResetDb   Also drop the SurrealDB volume so next dev-up is fresh."
Write-Host ""
Write-Host "  Default behavior (no flags):"
Write-Host "    * Stops + removes containers"
Write-Host "    * PRESERVES the SurrealDB volume so data survives"
Write-Host ""

exit 0
