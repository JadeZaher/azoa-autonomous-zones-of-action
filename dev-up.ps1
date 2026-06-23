<#
.SYNOPSIS
    AZOA Sleek -- full-stack dev launcher (PowerShell).

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

.PARAMETER NoBuild
    Skip the WebAPI/Frontend image + SDK rebuild. Default is to always
    rebuild so code changes are guaranteed to ship into the running
    container -- the #1 source of "I rebuilt locally and nothing changed"
    confusion.

.PARAMETER ResetDb
    DESTRUCTIVE. Tear down with `down -v --remove-orphans` so the
    SurrealDB volume is wiped before bringing the stack back up. Use when
    you genuinely want a clean DB. Default behavior preserves the volume
    across restarts -- code-only iteration should not lose data.

.PARAMETER Rebuild
    DEPRECATED -- rebuild is the default. Kept as a no-op alias.

.PARAMETER Clean
    Alias for -ResetDb. Kept for back-compat with older muscle memory.

.PARAMETER Preserve
    DEPRECATED -- volume preservation is the default. Kept as a no-op
    alias so older invocations don't error.

.PARAMETER Reset
    Destructively wipe the SurrealDB namespace and re-apply every schema
    + migration WITHOUT touching the volume itself. Pair with -ResetDb
    for a total reset; use alone when you want fresh schema on existing
    on-disk storage.

.EXAMPLE
    ./dev-up.ps1                # default: rebuild images + SDK, keep DB volume, apply pending schema
    ./dev-up.ps1 -NoBuild       # fast restart, reuse cached images
    ./dev-up.ps1 -ResetDb       # wipe DB volume + rebuild + fresh schema
    ./dev-up.ps1 -Reset         # keep volume but wipe + re-apply schema
    ./dev-up.ps1 -Logs          # tail combined logs after startup
#>
[CmdletBinding()]
param(
    [switch]$Logs,
    [switch]$NoBuild,
    [switch]$ResetDb,
    [switch]$Preserve,
    [switch]$Rebuild,
    [switch]$Clean,
    [switch]$Reset
)

# Derived state: rebuild is ON by default (opt out via -NoBuild); volume
# wipe is OFF by default (opt in via -ResetDb / -Clean). The deprecated
# -Rebuild / -Preserve flags are no-op aliases so older tabs don't error.
$DoRebuild = -not $NoBuild
$DoWipe    = $false
if ($Rebuild) { $DoRebuild = $true }   # explicit opt-in is harmless
if ($ResetDb) { $DoWipe    = $true }
if ($Clean)   { $DoWipe    = $true }   # legacy alias

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

# Project name (used below for both volume pruning and podman image tags).
$ProjectName = Split-Path -Leaf $ScriptDir

# ── Teardown (default: wipe volumes; -Preserve to keep) ──────────────────────

if ($DoWipe) {
    Write-Host "[dev-up] -ResetDb: tearing down stack + wiping SurrealDB volume..."
    Invoke-Compose @('-f', $ComposeFile, 'down', '-v', '--remove-orphans')
    # `volume` is an engine subcommand, not a compose one -- docker-compose /
    # podman-compose have no `volume ls`. Drive the engine binary directly
    # (mirrors dev-down.ps1).
    $volRuntime = if ($compose.Exe -like '*podman*') { 'podman' } else { 'docker' }
    if (Get-Command $volRuntime -ErrorAction SilentlyContinue) {
        try {
            $stale = & $volRuntime volume ls --filter "label=com.docker.compose.project=$ProjectName" -q 2>$null
            if ($LASTEXITCODE -eq 0 -and $stale) {
                Write-Host "[dev-up] Pruning $($stale.Count) stray project volume(s)..."
                & $volRuntime volume rm -f @stale 2>$null | Out-Null
            }
        } catch { }
    }
} else {
    Write-Host "[dev-up] Preserving SurrealDB volume across restart (pass -ResetDb to wipe)."
}

# ── Detect a pre-existing SurrealDB on localhost:8000 ─────────────────────────
#
# If the host already has a healthy SurrealDB on the canonical port (the
# user's own dev instance, common in this repo), skip the bundled
# `surrealdb` service to avoid the port collision. The API container
# instead points at the host via `host.containers.internal` (podman) /
# `host.docker.internal` (docker).

if (-not $env:SURREALDB_HOST_PORT) { $env:SURREALDB_HOST_PORT = '8000' }
$SurrealHostPort = $env:SURREALDB_HOST_PORT

# Snapshot AZOA_SURREAL_URL so the host.containers.internal / 127.0.0.1
# rewrites below don't leak into the caller's shell. Restored in finally.
$AzoaSurrealUrlEntry = $env:AZOA_SURREAL_URL
try {

# A responder on the host port is only "external" when it ISN'T our own
# bundled container. After a dev-up restart the bundled surrealdb is already
# up and answering on the mapped host port; treating that as external would
# wrongly skip the surrealdb service and point the API at host.docker.internal.
$bundledRunning = $false
$psRuntime = if ($compose.Exe -like '*podman*') { 'podman' } else { 'docker' }
if (Get-Command $psRuntime -ErrorAction SilentlyContinue) {
    $names = & $psRuntime ps --filter 'name=azoa-dev-surrealdb' --format '{{.Names}}' 2>$null
    if (-not [string]::IsNullOrWhiteSpace(($names -join ''))) { $bundledRunning = $true }
}

$ExistingSurrealDb = $false
if (-not $bundledRunning) {
    try {
        $null = Invoke-WebRequest -Uri "http://127.0.0.1:$SurrealHostPort/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
        $ExistingSurrealDb = $true
        Write-Host "[dev-up] Detected an existing SurrealDB on localhost:$SurrealHostPort -- reusing it."
    } catch { }
}

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
    # AZOA_SURREAL_URL drives BOTH the schema CLI's --connection and the
    # WebAPI's SurrealDb:Endpoint (the compose file interpolates the same
    # value into both). Single-underscore-safe -- podman-compose's
    # ${VAR:-default} parser drops names with double underscores.
    $env:AZOA_SURREAL_URL    = "http://${HostDbInternal}:$SurrealHostPort"
    $ComposeUpServices        = @('azoa-api', 'azoa-frontend')
} else {
    $ComposeUpServices        = @()  # empty == all services
}

# ── Guard: container shell scripts MUST be LF ─────────────────────────────────
#
# A Windows checkout with core.autocrlf=true rewrites *.sh to CRLF on disk. A
# CRLF shebang gets baked into the image and the container dies at startup with
#   /usr/bin/env: 'sh\r': No such file or directory
# .gitattributes now pins these to eol=lf, but a stale working-tree copy (or a
# checkout before the attribute landed) can still slip through -- so normalise
# in-place here, BEFORE any image build, and warn loudly so it gets noticed.

$ContainerScripts = @("docker-entrypoint.sh")   # scripts COPYd into images (see Dockerfile)
foreach ($rel in $ContainerScripts) {
    $f = Join-Path $ScriptDir $rel
    if (-not (Test-Path $f)) { continue }
    $bytes = [System.IO.File]::ReadAllBytes($f)
    if ($bytes -contains 13) {   # 13 = CR; presence means CRLF (or stray CR)
        Write-Host "[dev-up] WARN: $rel had CRLF line endings -- normalising to LF before build." -ForegroundColor Yellow
        Write-Host "[dev-up]       (a CRLF shebang would crash the container: `"env: 'sh\r'...`")" -ForegroundColor Yellow
        $text = [System.Text.Encoding]::UTF8.GetString($bytes) -replace "`r`n", "`n" -replace "`r", "`n"
        # Write LF-only UTF-8 with NO BOM so the shebang stays valid.
        [System.IO.File]::WriteAllText($f, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
}

# ── SDK rebuild (host-side dist; container build does its own tsup pass) ────
#
# This is a convenience for host-run frontend dev only — the compose flow
# rebuilds the SDK from source via tsup inside the frontend image, so the
# host dist is not consumed by the containers. tsup writes benign warnings
# (e.g. "Generated an empty chunk") to stderr; with $ErrorActionPreference =
# 'Stop' a native command's stderr would otherwise abort the whole script, so
# we temporarily relax it here and key success strictly on the exit code.

$SdkDir = Join-Path $ScriptDir "sdk/azoa-wallet"
if ($DoRebuild -and (Test-Path $SdkDir)) {
    Write-Host "[dev-up] Rebuilding @azoa/wallet-sdk (host-side dist)..."
    Push-Location $SdkDir
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if (-not (Test-Path (Join-Path $SdkDir "node_modules"))) {
            & npm install --silent 2>&1 | Write-Host
            if ($LASTEXITCODE -ne 0) { Write-Error "[dev-up] SDK npm install failed."; exit 1 }
        }
        & npm run build --silent 2>&1 | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Error "[dev-up] SDK build (tsup) failed."; exit 1 }
    } finally {
        $ErrorActionPreference = $prevEap
        Pop-Location
    }
}

# ── Workaround: podman-compose v1.5.0 silently ignores `dockerfile:` ──────────
#
# When two services share `context: .` but use different Dockerfiles,
# podman-compose builds BOTH with the same Dockerfile (whichever it picks
# first) and tags both images identically. Detection: $compose.Exe contains
# 'podman'. Workaround: hand-build each image with `podman build -f` so
# compose `up` finds matching pre-built tags and skips its broken builder.
# docker compose v2 / docker-compose v1 honour `dockerfile:` correctly.

function Test-PodmanImage {
    param([string]$Tag)
    & podman image exists $Tag
    return ($LASTEXITCODE -eq 0)
}

if ($compose.Exe -like '*podman*') {
    $apiImage      = "localhost/${ProjectName}_azoa-api:latest"
    $frontendImage = "localhost/${ProjectName}_azoa-frontend:latest"
    $apiCached      = Test-PodmanImage $apiImage
    $frontendCached = Test-PodmanImage $frontendImage
    if ($DoRebuild -or -not $apiCached -or -not $frontendCached) {
        Write-Host "[dev-up] podman runtime detected -- pre-building images per Dockerfile"
        Write-Host "[dev-up]   (works around podman-compose v1.5.0 'dockerfile:' bug)"
        & podman build -f Dockerfile -t $apiImage $ScriptDir
        if ($LASTEXITCODE -ne 0) { Write-Error "[dev-up] podman build (API) failed."; exit 1 }
        & podman build -f frontend/Dockerfile -t $frontendImage $ScriptDir
        if ($LASTEXITCODE -ne 0) { Write-Error "[dev-up] podman build (frontend) failed."; exit 1 }
    } else {
        Write-Host "[dev-up] -NoBuild + cached images present: skipping rebuild."
    }
}

# ── Build + start ────────────────────────────────────────────────────────────

$upArgs = @('-f', $ComposeFile, 'up', '-d', '--remove-orphans')
# Don't pass --build for podman runtimes -- we already pre-built above,
# and triggering compose's broken builder would re-tag azoa-frontend with
# the wrong image content.
if ($DoRebuild -and ($compose.Exe -notlike '*podman*')) { $upArgs += '--build' }
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

# ── SurrealDB schema sync ─────────────────────────────────────────────────────
#
# Default: run `azoa-surreal up` -- idempotent. The CLI tracks applied files
# via the schema_migration table, so re-running is a no-op when nothing has
# changed and applies only pending files when there's drift. This is the
# "newcomer clones the repo and runs ./dev-up.ps1" path.
#
# -Reset: destructive wipe + full re-apply (delegates to `reset` verb).
# AZOA_SKIP_RESET=1: skip the schema step entirely (preserves DB state,
#   useful when iterating on UI without touching schema).

if ($env:AZOA_SKIP_RESET -eq "1") {
    Write-Host "[dev-up] AZOA_SKIP_RESET=1 set -- skipping schema sync"
} else {
    if (-not $env:AZOA_SURREAL_NS)   { $env:AZOA_SURREAL_NS   = 'azoa' }
    if (-not $env:AZOA_SURREAL_DB)   { $env:AZOA_SURREAL_DB   = 'azoa' }
    if (-not $env:AZOA_SURREAL_USER) { $env:AZOA_SURREAL_USER = 'root' }
    if (-not $env:AZOA_SURREAL_PASS) { $env:AZOA_SURREAL_PASS = 'root' }

    # Schema CLI runs on the HOST. The AZOA_SURREAL_URL set earlier for
    # the API container points at host.containers.internal which won't
    # resolve here -- override for the CLI call, restore after.
    $schemaUrlBackup = $env:AZOA_SURREAL_URL
    $env:AZOA_SURREAL_URL = "http://127.0.0.1:$SurrealHostPort"

    # Wait for SurrealDB to be reachable (the container case needs a beat).
    $surrealReady = $false
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $null = Invoke-WebRequest -Uri "$env:AZOA_SURREAL_URL/health" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
            $surrealReady = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $surrealReady) {
        $probedUrl = $env:AZOA_SURREAL_URL
        $env:AZOA_SURREAL_URL = $schemaUrlBackup
        Write-Host ""
        Write-Host "[dev-up] SurrealDB at $probedUrl never became reachable." -ForegroundColor Yellow
        Write-Host "[dev-up] Dumping bundled surrealdb container state + logs to diagnose:" -ForegroundColor Yellow
        $runtime = if ($compose.Exe -like '*podman*') { 'podman' } else { 'docker' }
        if (Get-Command $runtime -ErrorAction SilentlyContinue) {
            Write-Host "---- $runtime ps (azoa-dev-surrealdb) ----"
            & $runtime ps -a --filter 'name=azoa-dev-surrealdb' --format '{{.Status}}  {{.Names}}' 2>&1
            Write-Host "---- $runtime logs --tail=50 azoa-dev-surrealdb ----"
            & $runtime logs --tail=50 azoa-dev-surrealdb 2>&1
            Write-Host "---- end ----"
        } else {
            Write-Host "[dev-up] (no '$runtime' on PATH to dump logs)"
        }
        Write-Host ""
        Write-Host "[dev-up] Common causes:" -ForegroundColor Yellow
        Write-Host "  * Storage URI rejected by surrealdb v3.1.4 (look for 'failed to parse' in the log above)."
        Write-Host "  * Rootless podman volume ownership (look for 'permission denied' on /data)."
        Write-Host "  * Port 8000 already bound on host (look for 'address already in use')."
        Write-Host "  * Set AZOA_SKIP_RESET=1 to bypass schema sync while you investigate."
        Write-Error "[dev-up] Aborting -- SurrealDB unreachable. Logs above should name the cause."
        exit 1
    }

    Write-Host ""
    if ($Reset) {
        Write-Host "[dev-up] -Reset: wiping + re-applying SurrealDB schema..."
        dotnet run --project packages/Azoa.SurrealDb.Schema --framework net8.0 -- reset
    } else {
        Write-Host "[dev-up] syncing SurrealDB schema (idempotent; use -Reset to wipe)..."
        dotnet run --project packages/Azoa.SurrealDb.Schema --framework net8.0 -- up
    }
    $schemaExit = $LASTEXITCODE
    $env:AZOA_SURREAL_URL = $schemaUrlBackup
    $global:LASTEXITCODE = $schemaExit
    if ($LASTEXITCODE -ne 0) {
        $verb = if ($Reset) { 'reset' } else { 'up' }
        Write-Error "SurrealDB $verb failed (exit $LASTEXITCODE). Set AZOA_SKIP_RESET=1 to skip, or pass -Reset to force a clean wipe."
        exit 1
    }
}

Write-Host ""
Write-Host "[dev-up] Endpoints:"
Write-Host "  WebAPI:    http://localhost:5000  (health: /health)"
Write-Host "  Frontend:  http://localhost:3000"
Write-Host "  SurrealDB: http://localhost:8000  (root / root)"
Write-Host ""
Write-Host "[dev-up] Tear down: ./dev-down.ps1"
Write-Host "[dev-up] Logs:      $runtimeLabel -f $ComposeFile logs -f <service>"
Write-Host ""
Write-Host "[dev-up] Flags (run ./dev-up.ps1 -<Flag>):"
Write-Host "  -NoBuild   Skip image + SDK rebuild. Fast restart, reuses cached images."
Write-Host "  -ResetDb   DESTRUCTIVE. Wipe SurrealDB volume before bringing the stack up."
Write-Host "             (alias: -Clean)"
Write-Host "  -Reset     Wipe + re-apply SurrealDB schema WITHOUT touching the volume."
Write-Host "             Combine with -ResetDb for a total reset."
Write-Host "  -Logs      Tail combined container logs after startup (Ctrl-C to stop)."
Write-Host ""
Write-Host "  Default behavior (no flags):"
Write-Host "    * Rebuilds API + Frontend images and host-side SDK dist"
Write-Host "    * PRESERVES the SurrealDB volume across restart"
Write-Host "    * Applies pending schema migrations idempotently"
Write-Host ""

if ($Logs) {
    Write-Host ""
    Write-Host "[dev-up] Tailing combined logs (Ctrl-C to stop):"
    Invoke-Compose @('-f', $ComposeFile, 'logs', '-f')
}
} finally {
    # Restore the caller's AZOA_SURREAL_URL so host.containers.internal /
    # 127.0.0.1 rewrites don't leak into the shell after the script exits.
    if ($null -eq $AzoaSurrealUrlEntry) {
        Remove-Item Env:AZOA_SURREAL_URL -ErrorAction SilentlyContinue
    } else {
        $env:AZOA_SURREAL_URL = $AzoaSurrealUrlEntry
    }
}
