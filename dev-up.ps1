<#
.SYNOPSIS
    OASIS Sleek -- full-stack dev launcher (PowerShell).

.DESCRIPTION
    Brings up SurrealDB + WebAPI + Frontend via docker-compose.dev.yml.
    Auto-detects docker compose v2, docker-compose v1, podman-compose, or
    the newer `podman compose` subcommand.

    After startup:
      * WebAPI:    http://localhost:5000 (health: /health)
      * Frontend:  http://localhost:3000
      * SurrealDB: http://localhost:8000 (root / root)

    Tear down: ./dev-down.ps1
    Status:    <runtime> -f docker-compose.dev.yml ps
    Logs:      <runtime> -f docker-compose.dev.yml logs -f <service>

.PARAMETER Logs
    After starting the stack, attach to combined logs (Ctrl-C to stop).

.PARAMETER Rebuild
    Force `--build` so the WebAPI and Frontend images are rebuilt from
    current source. Use after a code change that affects either image.

.PARAMETER Clean
    `down -v` before bringing the stack up so the surrealdb_data volume
    is wiped. Use when you want a fresh DB.

.EXAMPLE
    ./dev-up.ps1
    ./dev-up.ps1 -Logs
    ./dev-up.ps1 -Rebuild
    ./dev-up.ps1 -Clean
#>
[CmdletBinding()]
param(
    [switch]$Logs,
    [switch]$Rebuild,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ComposeFile = Join-Path $ScriptDir "docker-compose.dev.yml"

if (-not (Test-Path $ComposeFile)) {
    Write-Error "[dev-up] FATAL: $ComposeFile not found."
    exit 1
}

# ── Detect compose runtime ────────────────────────────────────────────────────
#
# Returns a hashtable @{ Exe='docker'; PreArgs=@('compose') }. PreArgs may
# be empty (`docker-compose`, `podman-compose`) or `@('compose')` for the
# subcommand form. The call site splats PreArgs followed by the per-call
# arguments so the leading executable token is never accidentally treated
# as an argument by PowerShell's call operator.
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
    Write-Error "[dev-up] FATAL: no compose runtime found. Install one of: Docker Desktop, docker-compose, podman-compose, podman 4.x+."
    exit 1
}

$runtimeLabel = ($compose.PreArgs + @($compose.Exe)) -join ' '
if ($compose.PreArgs.Count -gt 0) {
    $runtimeLabel = "$($compose.Exe) $($compose.PreArgs -join ' ')"
}
Write-Host "[dev-up] Using compose runtime: $runtimeLabel"

# Run helper: $compose.Exe <PreArgs...> <args...>
# Parameter is named $Arguments (not $Args) because $Args is a PowerShell
# automatic variable -- shadowing it triggers PSAvoidAssignmentToAutomaticVariable.
function Invoke-Compose {
    param([string[]]$Arguments)
    $full = @()
    $full += $compose.PreArgs
    $full += $Arguments
    & $compose.Exe @full
}

# ── Optional clean ────────────────────────────────────────────────────────────

if ($Clean) {
    Write-Host "[dev-up] -Clean: tearing down with -v (wipes SurrealDB volume) ..."
    Invoke-Compose @('-f', $ComposeFile, 'down', '-v', '--remove-orphans')
}

# ── Detect a pre-existing SurrealDB on localhost:8000 ─────────────────────────
#
# If the host already has a healthy SurrealDB on the canonical port (the
# user's own dev instance, common in this repo), skip the bundled
# `surrealdb` service to avoid the port collision. The API container
# instead points at the host via `host.containers.internal` (podman) /
# `host.docker.internal` (docker).

$ExistingSurrealDb = $false
try {
    $null = Invoke-WebRequest -Uri "http://127.0.0.1:8000/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
    $ExistingSurrealDb = $true
    Write-Host "[dev-up] Detected an existing SurrealDB on localhost:8000 -- reusing it."
} catch { }

# Compose-up service set: omit `surrealdb` when an external instance is available.
if ($ExistingSurrealDb) {
    $HostDbInternal = if ($compose.Exe -like '*podman*') {
        'host.containers.internal'
    } else {
        'host.docker.internal'
    }
    # Don't set SURREALDB_HOST_PORT here -- the bundled surrealdb service
    # is omitted from the `up` set below, so its port mapping is never
    # parsed at runtime, but podman-compose still validates the field at
    # config-load time. The default `8000` lets that validation pass; the
    # service simply doesn't start.
    # OASIS_SURREAL_URL drives BOTH the schema CLI's --connection and the
    # WebAPI's SurrealDb:Endpoint (the compose file interpolates the same
    # value into both). Single-underscore-safe -- podman-compose's
    # ${VAR:-default} parser drops names with double underscores.
    $env:OASIS_SURREAL_URL    = "http://${HostDbInternal}:8000"
    $ComposeUpServices        = @('oasis-api', 'oasis-frontend')
} else {
    $ComposeUpServices        = @()  # empty == all services
}

# ── Workaround: podman-compose v1.5.0 silently ignores `dockerfile:` ──────────
#
# When two services share `context: .` but use different Dockerfiles,
# podman-compose builds BOTH with the same Dockerfile (whichever it picks
# first) and tags both images identically. Detection: $compose.Exe contains
# 'podman'. Workaround: hand-build each image with `podman build -f` so
# compose `up` finds matching pre-built tags and skips its broken builder.
# docker compose v2 / docker-compose v1 honour `dockerfile:` correctly.

$ProjectName = Split-Path -Leaf $ScriptDir

function Test-PodmanImage {
    param([string]$Tag)
    & podman image exists $Tag
    return ($LASTEXITCODE -eq 0)
}

if ($compose.Exe -like '*podman*') {
    $apiImage      = "localhost/${ProjectName}_oasis-api:latest"
    $frontendImage = "localhost/${ProjectName}_oasis-frontend:latest"
    $apiCached      = Test-PodmanImage $apiImage
    $frontendCached = Test-PodmanImage $frontendImage
    if ($Rebuild -or -not $apiCached -or -not $frontendCached) {
        Write-Host "[dev-up] podman runtime detected -- pre-building images per Dockerfile"
        Write-Host "[dev-up]   (works around podman-compose v1.5.0 'dockerfile:' bug)"
        & podman build -f Dockerfile -t $apiImage $ScriptDir
        if ($LASTEXITCODE -ne 0) { Write-Error "[dev-up] podman build (API) failed."; exit 1 }
        & podman build -f frontend/Dockerfile -t $frontendImage $ScriptDir
        if ($LASTEXITCODE -ne 0) { Write-Error "[dev-up] podman build (frontend) failed."; exit 1 }
    } else {
        Write-Host "[dev-up] podman runtime: images already cached. Use -Rebuild to force."
    }
}

# ── Build + start ────────────────────────────────────────────────────────────

$upArgs = @('-f', $ComposeFile, 'up', '-d', '--remove-orphans')
# Don't pass --build for podman runtimes -- we already pre-built above,
# and triggering compose's broken builder would re-tag oasis-frontend with
# the wrong image content.
if ($Rebuild -and ($compose.Exe -notlike '*podman*')) { $upArgs += '--build' }
# When an external SurrealDB was detected, only bring up the API + frontend.
# --no-deps tells compose to ignore depends_on so it doesn't try to start
# the bundled surrealdb service (which would collide on port 8000).
if ($ComposeUpServices.Count -gt 0) {
    $upArgs += '--no-deps'
    $upArgs += $ComposeUpServices
}

Write-Host "[dev-up] Starting stack ..."
Invoke-Compose $upArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "[dev-up] compose up failed (exit $LASTEXITCODE)."
    exit 1
}

Write-Host "[dev-up] Stack started. Service status:"
Invoke-Compose @('-f', $ComposeFile, 'ps')

# ── SurrealDB namespace reset ─────────────────────────────────────────────────
#
# Wipe + re-apply all schema migrations so the dev DB is always in sync with
# the current schema files. Skip via OASIS_SKIP_RESET=1 when you want to
# preserve existing data (e.g. iterating on UI without re-seeding).

if ($env:OASIS_SKIP_RESET -ne "1") {
    Write-Host ""
    Write-Host "[dev-up] resetting SurrealDB namespace..."
    dotnet run --project packages/Oasis.SurrealDb.Schema --framework net8.0 -- reset
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SurrealDB reset failed (exit $LASTEXITCODE). Set OASIS_SKIP_RESET=1 to skip."
        exit 1
    }
} else {
    Write-Host "[dev-up] OASIS_SKIP_RESET=1 set -- skipping reset"
}

Write-Host ""
Write-Host "[dev-up] Endpoints:"
Write-Host "  WebAPI:    http://localhost:5000  (health: /health)"
Write-Host "  Frontend:  http://localhost:3000"
Write-Host "  SurrealDB: http://localhost:8000  (root / root)"
Write-Host ""
Write-Host "[dev-up] Tear down: ./dev-down.ps1"
Write-Host "[dev-up] Logs:      $runtimeLabel -f $ComposeFile logs -f <service>"

if ($Logs) {
    Write-Host ""
    Write-Host "[dev-up] Tailing combined logs (Ctrl-C to stop):"
    Invoke-Compose @('-f', $ComposeFile, 'logs', '-f')
}
