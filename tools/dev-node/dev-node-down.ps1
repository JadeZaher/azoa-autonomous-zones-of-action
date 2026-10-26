<#
.SYNOPSIS
    Stops exactly what tools/dev-node/dev-node-up.ps1 recorded in .omc/state/dev-node/pids.json
    (WebAPI, KYC sidecar, SurrealDB container). Never matches processes by name. PowerShell 5.1-compatible.

.PARAMETER Only
    Restrict to these recorded entries (webapi, sidecar, surrealdb). Used by dev-node-up's failure path.

.PARAMETER KeepSurreal
    Leave the SurrealDB container running (faster next up; data is in the named volume either way).
#>
[CmdletBinding()]
param(
    [string[]]$Only = @('webapi', 'sidecar', 'surrealdb'),
    [switch]$KeepSurreal
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-node-common.ps1')

$recorded = Get-RecordedPids

# Order matters: the WebAPI first so it stops calling the sidecar/DB while they go away.
foreach ($name in @('webapi', 'sidecar')) {
    if ($Only -notcontains $name) { continue }
    $entry = Get-Field $recorded $name
    if ($null -eq $entry) { Write-Step "${name}: nothing recorded."; continue }
    Stop-OwnedProcess -Name $name -Entry $entry
    Remove-RecordedEntry $recorded $name
}

$surreal = Get-Field $recorded 'surrealdb'
if ($Only -contains 'surrealdb' -and -not $KeepSurreal) {
    if ($null -eq $surreal) {
        Write-Step 'surrealdb: not started by dev-node-up; leaving it alone.'
    } else {
        $engine = Get-Field $surreal 'engine'
        $container = Get-Field $surreal 'container'
        if ($null -ne $engine -and (Get-Command $engine -ErrorAction SilentlyContinue)) {
            Write-Step "Stopping container $container ($engine) ..."
            $stopped = Invoke-Native $engine @('stop', $container)
            if ($stopped.Code -eq 0) { Write-Step "$container stopped (volume preserved)." }
            else { Write-Warn "Could not stop ${container}: $($stopped.Output)" }
        } else {
            Write-Warn "Recorded engine '$engine' is not on PATH; stop $container manually."
        }
        Remove-RecordedEntry $recorded 'surrealdb'
    }
}

Save-RecordedPids $recorded
# The env file describes a live node; once the WebAPI is down it would only mislead a consumer.
if (($Only -contains 'webapi') -and (Test-Path $EnvFile)) {
    Remove-Item -Force $EnvFile
    Write-Step "Removed $EnvFile (tenant keys stay in $TenantFile for reuse)."
}
Write-Step 'Down complete.'
