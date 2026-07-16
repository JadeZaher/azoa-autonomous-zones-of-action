<#
.SYNOPSIS
    AZOA -- SurrealDB backup (surreal export via container exec).

.DESCRIPTION
    Exports a SurrealDB namespace/database to a SurrealQL script by running
    `surreal export` INSIDE the SurrealDB container (the endpoint is reached
    over the container's own loopback), then copying the resulting file out
    to the host via `<runtime> cp`. See scripts/surrealdb/AGENTS.md for why.

.PARAMETER OutputPath
    Host path to write the exported .surql file to.

.PARAMETER Namespace
    SurrealDB namespace to export.

.PARAMETER Database
    SurrealDB database to export.

.PARAMETER Endpoint
    SurrealDB HTTP endpoint AS SEEN FROM INSIDE THE CONTAINER. Defaults to
    http://localhost:8000 (the container's own bind address) -- this is
    intentionally NOT the host-mapped endpoint, since surreal export/import
    runs as a subprocess inside the container, not from the host.

.PARAMETER User
    SurrealDB root username. Defaults to 'root'.

.PARAMETER Pass
    SurrealDB root password. Defaults to 'root'.

.PARAMETER ContainerName
    Name of the running SurrealDB container. Defaults to 'azoa-dev-surrealdb'
    (the docker-compose.dev.yml service name).

.EXAMPLE
    ./backup.ps1 -OutputPath ./backup.surql -Namespace azoa -Database azoa
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$Namespace,

    [Parameter(Mandatory = $true)]
    [string]$Database,

    [string]$Endpoint = 'http://localhost:8000',

    [string]$User = 'root',

    [string]$Pass = 'root',

    [string]$ContainerName = 'azoa-dev-surrealdb'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ContainerRuntime.ps1')

$runtime = Find-ContainerRuntime
Write-Host "[backup] Using container runtime: $runtime"
Write-Host "[backup] Container: $ContainerName  NS: $Namespace  DB: $Database"

# surreal export runs INSIDE the container; write to a container-local temp
# path, then copy it out. Using a per-invocation name avoids collisions with
# concurrent backups against the same container.
$containerFile = "/tmp/azoa-backup-$([Guid]::NewGuid().ToString('N')).surql"

$exportArgs = @(
    'exec', $ContainerName,
    '/surreal', 'export',
    '--endpoint', $Endpoint,
    '--username', $User,
    '--password', $Pass,
    '--namespace', $Namespace,
    '--database', $Database,
    $containerFile
)

Write-Host "[backup] Running: $runtime $($exportArgs -join ' ')"
& $runtime @exportArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "[backup] FATAL: surreal export failed inside container '$ContainerName' (exit $LASTEXITCODE)."
    exit 1
}

# Ensure the host output directory exists before copying.
$outDir = Split-Path -Parent $OutputPath
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$cpArgs = @('cp', "${ContainerName}:${containerFile}", $OutputPath)
Write-Host "[backup] Running: $runtime $($cpArgs -join ' ')"
& $runtime @cpArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "[backup] FATAL: failed to copy export file out of container (exit $LASTEXITCODE)."
    exit 1
}

# Best-effort cleanup of the container-local temp file (image has no shell,
# so we can't `rm` via exec; leaving a small .surql in /tmp is harmless and
# is cleared on container restart since /tmp is not a persisted volume).

if (-not (Test-Path $OutputPath) -or (Get-Item $OutputPath).Length -eq 0) {
    Write-Error "[backup] FATAL: output file '$OutputPath' missing or empty after copy."
    exit 1
}

Write-Host "[backup] OK: wrote $((Get-Item $OutputPath).Length) bytes to $OutputPath"
exit 0
