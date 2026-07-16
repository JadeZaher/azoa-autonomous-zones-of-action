<#
.SYNOPSIS
    AZOA -- SurrealDB restore (surreal import via container exec).

.DESCRIPTION
    Restores a SurrealQL script produced by backup.ps1 by copying it INTO the
    SurrealDB container (via `<runtime> cp`) and then running `surreal import`
    inside the container against the given namespace/database. See
    scripts/surrealdb/AGENTS.md for why exec-based export/import is used
    instead of the host-side surreal CLI.

.PARAMETER InputPath
    Host path to the .surql file to restore (as produced by backup.ps1).

.PARAMETER Namespace
    SurrealDB namespace to restore into.

.PARAMETER Database
    SurrealDB database to restore into.

.PARAMETER Endpoint
    SurrealDB HTTP endpoint AS SEEN FROM INSIDE THE CONTAINER. Defaults to
    http://localhost:8000.

.PARAMETER User
    SurrealDB root username. Defaults to 'root'.

.PARAMETER Pass
    SurrealDB root password. Defaults to 'root'.

.PARAMETER ContainerName
    Name of the running SurrealDB container. Defaults to 'azoa-dev-surrealdb'.

.PARAMETER Force
    Non-interactive: skip the confirmation prompt. Required for CI / gate-test
    invocation since there is no TTY to confirm against.

.EXAMPLE
    ./restore.ps1 -InputPath ./backup.surql -Namespace azoa -Database azoa -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$Namespace,

    [Parameter(Mandatory = $true)]
    [string]$Database,

    [string]$Endpoint = 'http://localhost:8000',

    [string]$User = 'root',

    [string]$Pass = 'root',

    [string]$ContainerName = 'azoa-dev-surrealdb',

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ContainerRuntime.ps1')

if (-not (Test-Path $InputPath)) {
    Write-Error "[restore] FATAL: input file '$InputPath' does not exist."
    exit 1
}

if (-not $Force) {
    $confirmation = Read-Host "This will replay '$InputPath' into $Namespace/$Database on container '$ContainerName'. Type 'yes' to continue"
    if ($confirmation -ne 'yes') {
        Write-Host "[restore] Aborted by operator."
        exit 1
    }
}

$runtime = Find-ContainerRuntime
Write-Host "[restore] Using container runtime: $runtime"
Write-Host "[restore] Container: $ContainerName  NS: $Namespace  DB: $Database"

$containerFile = "/tmp/azoa-restore-$([Guid]::NewGuid().ToString('N')).surql"

$cpArgs = @('cp', $InputPath, "${ContainerName}:${containerFile}")
Write-Host "[restore] Running: $runtime $($cpArgs -join ' ')"
& $runtime @cpArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "[restore] FATAL: failed to copy '$InputPath' into container (exit $LASTEXITCODE)."
    exit 1
}

$importArgs = @(
    'exec', $ContainerName,
    '/surreal', 'import',
    '--endpoint', $Endpoint,
    '--username', $User,
    '--password', $Pass,
    '--namespace', $Namespace,
    '--database', $Database,
    $containerFile
)

Write-Host "[restore] Running: $runtime $($importArgs -join ' ')"
& $runtime @importArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "[restore] FATAL: surreal import failed inside container '$ContainerName' (exit $LASTEXITCODE)."
    exit 1
}

Write-Host "[restore] OK: restored $InputPath into $Namespace/$Database"
exit 0
