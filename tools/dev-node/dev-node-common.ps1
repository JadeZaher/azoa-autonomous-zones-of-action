# Shared helpers for dev-node-up.ps1 / dev-node-down.ps1 (dot-sourced). PowerShell 5.1-compatible.
# See tools/dev-node/AGENTS.md for the lifecycle, state files, and ownership rules.

$script:RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:StateRoot  = Join-Path $script:RepoRoot '.omc\state'
$script:StateDir   = Join-Path $script:StateRoot 'dev-node'
$script:LogDir     = Join-Path $script:StateDir 'logs'
$script:PidsFile   = Join-Path $script:StateDir 'pids.json'
$script:SecretFile = Join-Path $script:StateDir 'dev-secrets.json'
$script:TenantFile = Join-Path $script:StateDir 'tenant.json'
$script:EnvFile    = Join-Path $script:StateRoot 'dev-node-env.json'
$script:SurrealContainer = 'azoa-dev-surrealdb'

function Write-Step { param([string]$Message) Write-Host "[dev-node] $Message" }
function Write-Warn { param([string]$Message) Write-Host "[dev-node] WARN: $Message" -ForegroundColor Yellow }

function Fail {
    param([string]$Message)
    Write-Host "[dev-node] FATAL: $Message" -ForegroundColor Red
    throw "dev-node: $Message"
}

function Initialize-StateDir {
    foreach ($dir in @($script:StateRoot, $script:StateDir, $script:LogDir)) {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
}

# UTF-8 without BOM: Node's JSON.parse rejects a BOM, and PS 5.1 Set-Content -Encoding UTF8 writes one.
function Write-JsonFile {
    param([string]$Path, $Value)
    $json = $Value | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    try { return (Get-Content -Raw -Path $Path | ConvertFrom-Json) } catch { return $null }
}

function New-RandomHex {
    param([int]$Bytes = 24)
    $buffer = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    return (($buffer | ForEach-Object { $_.ToString('x2') }) -join '')
}

# Runs a native command with EAP relaxed (PS 5.1 turns redirected stderr into terminating errors).
function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        return @{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
    } finally {
        $ErrorActionPreference = $previous
    }
}

# HTTP helper that never throws on non-2xx: returns @{ Status; Body; Raw }.
function Invoke-Api {
    param(
        [string]$Method,
        [string]$Url,
        $Body = $null,
        [hashtable]$Headers = @{},
        [int]$TimeoutSec = 30
    )
    $params = @{ Method = $Method; Uri = $Url; UseBasicParsing = $true; Headers = $Headers; TimeoutSec = $TimeoutSec; ErrorAction = 'Stop' }
    if ($null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 10 -Compress)
        $params.ContentType = 'application/json'
    }
    try {
        $response = Invoke-WebRequest @params
        return @{ Status = [int]$response.StatusCode; Body = (ConvertFrom-JsonOrNull $response.Content); Raw = $response.Content }
    } catch {
        $response = $_.Exception.Response
        if ($null -eq $response) { return @{ Status = 0; Body = $null; Raw = $_.Exception.Message } }
        $text = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $text = $_.ErrorDetails.Message }
        return @{ Status = [int]$response.StatusCode; Body = (ConvertFrom-JsonOrNull $text); Raw = $text }
    }
}

function ConvertFrom-JsonOrNull {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    try { return ($Text | ConvertFrom-Json) } catch { return $null }
}

function Test-HttpOk {
    param([string]$Url, [int]$TimeoutSec = 3)
    $result = Invoke-Api -Method 'GET' -Url $Url -TimeoutSec $TimeoutSec
    return ($result.Status -ge 200 -and $result.Status -lt 300)
}

# Sets process env vars only for the duration of $Action, then restores them (callers may dot-source/& us).
function Invoke-WithEnvironment {
    param([hashtable]$Variables, [scriptblock]$Action)
    $saved = @{}
    foreach ($name in $Variables.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, [string]$Variables[$name], 'Process')
    }
    try { return (& $Action) }
    finally {
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}

function Get-Field {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Show-LogTail {
    param([string]$Path, [int]$Lines = 40)
    if (Test-Path $Path) {
        Write-Host "----- tail $Path -----"
        Get-Content -Path $Path -Tail $Lines | ForEach-Object { Write-Host "  $_" }
    }
}

# ── PID ownership: only processes this tooling started, verified by start time ──

function Get-RecordedPids {
    $recorded = Read-JsonFile $script:PidsFile
    if ($null -eq $recorded) { return [pscustomobject]@{} }
    return $recorded
}

function Save-RecordedPids {
    param($Recorded)
    Initialize-StateDir
    Write-JsonFile -Path $script:PidsFile -Value $Recorded
}

function Get-ProcessStartUtc {
    param([System.Diagnostics.Process]$Process)
    try { return $Process.StartTime.ToUniversalTime().ToString('o') } catch { return $null }
}

# Returns the live Process only if it is the exact one recorded (same PID AND same start time).
function Get-OwnedProcess {
    param($Entry)
    $recordedPid = Get-Field $Entry 'pid'
    $recordedStart = Get-Field $Entry 'startedAt'
    if ($null -eq $recordedPid -or $null -eq $recordedStart) { return $null }
    $process = Get-Process -Id ([int]$recordedPid) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    $started = Get-ProcessStartUtc $process
    if ($null -eq $started) { return $null }
    # PS 7's ConvertFrom-Json already yields a DateTime; PS 5.1 yields the ISO string.
    if ($recordedStart -is [datetime]) { $recordedUtc = $recordedStart.ToUniversalTime() }
    else { $recordedUtc = [datetime]::Parse([string]$recordedStart, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
    $delta = [Math]::Abs(($process.StartTime.ToUniversalTime() - $recordedUtc).TotalSeconds)
    if ($delta -gt 2) { return $null }
    return $process
}

function Stop-OwnedProcess {
    param([string]$Name, $Entry)
    $process = Get-OwnedProcess $Entry
    if ($null -eq $process) {
        if ($null -ne $Entry) { Write-Step "$Name (pid $($Entry.pid)) is not running (or the PID was reused); nothing to stop." }
        return
    }
    Write-Step "Stopping $Name (pid $($process.Id)) ..."
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    try { $process.WaitForExit(15000) | Out-Null } catch { }
    if ($process.HasExited) { Write-Step "$Name stopped." } else { Write-Warn "$Name (pid $($process.Id)) did not exit within 15s." }
}

function Set-RecordedEntry {
    param($Recorded, [string]$Name, $Value)
    if ($Recorded.PSObject.Properties[$Name]) { $Recorded.$Name = $Value }
    else { $Recorded | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

function Remove-RecordedEntry {
    param($Recorded, [string]$Name)
    if ($Recorded.PSObject.Properties[$Name]) { $Recorded.PSObject.Properties.Remove($Name) }
}

# ── Container runtime ────────────────────────────────────────────────────────

# Returns @{ Engine; ComposeExe; ComposePreArgs } for a RUNNING engine, or @{ Error = '...' }.
function Find-ContainerRuntime {
    $reasons = @()
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $info = Invoke-Native 'docker' @('info', '--format', '{{.ServerVersion}}')
        if ($info.Code -eq 0) {
            $compose = Invoke-Native 'docker' @('compose', 'version')
            if ($compose.Code -eq 0) { return @{ Engine = 'docker'; ComposeExe = 'docker'; ComposePreArgs = @('compose') } }
            if (Get-Command docker-compose -ErrorAction SilentlyContinue) { return @{ Engine = 'docker'; ComposeExe = 'docker-compose'; ComposePreArgs = @() } }
            $reasons += 'docker engine is running but no compose plugin was found'
        } else {
            $reasons += 'docker is installed but its engine is not reachable'
        }
    }
    if (Get-Command podman -ErrorAction SilentlyContinue) {
        $info = Invoke-Native 'podman' @('info', '--format', '{{.Host.Arch}}')
        if ($info.Code -eq 0) {
            $compose = Invoke-Native 'podman' @('compose', 'version')
            if ($compose.Code -eq 0) { return @{ Engine = 'podman'; ComposeExe = 'podman'; ComposePreArgs = @('compose') } }
            if (Get-Command podman-compose -ErrorAction SilentlyContinue) { return @{ Engine = 'podman'; ComposeExe = 'podman-compose'; ComposePreArgs = @() } }
            $reasons += 'podman is running but no compose provider was found'
        } else {
            $reasons += 'podman is installed but its machine is not running (podman machine start, or pass -StartPodmanMachine)'
        }
    }
    if ($reasons.Count -eq 0) { $reasons += 'neither docker nor podman is installed' }
    return @{ Error = ($reasons -join '; ') }
}

# Host port mapped to the container's 8000, or $null when the container is not running.
function Get-SurrealContainerHostPort {
    param([string]$Engine)
    $running = Invoke-Native $Engine @('ps', '--filter', "name=$($script:SurrealContainer)", '--format', '{{.Names}}')
    if ($running.Code -ne 0 -or ($running.Output -notmatch [regex]::Escape($script:SurrealContainer))) { return $null }
    $port = Invoke-Native $Engine @('port', $script:SurrealContainer, '8000')
    if ($port.Code -ne 0) { return $null }
    $match = [regex]::Match($port.Output, ':(\d+)\s*$', 'Multiline')
    if (-not $match.Success) { return $null }
    return [int]$match.Groups[1].Value
}
