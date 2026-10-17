# P2 sandbox data-compatibility rehearsal (round 4, D-9 generation reset). See AGENTS.md beside this file.
# Fresh throwaway SurrealDB 3.1.4 (RocksDB, --user 0:0) -> deploy path (`surrealforge up --no-reconcile`) twice
# (second must be a no-op) -> GeneratedSchemaDataCompatibilityTests against that sandbox -> container reaped.
[CmdletBinding()]
param(
    [int] $Port = 8049,
    [string] $Runtime = $(if ($env:AZOA_CONTAINER_RUNTIME) { $env:AZOA_CONTAINER_RUNTIME } else { 'podman' }),
    [string] $Image = 'docker.io/surrealdb/surrealdb:v3.1.4'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$container = 'azoa-p2-rehearsal'
if ($Port -lt 8041 -or $Port -gt 8049) { throw 'Rehearsal ports are 8041-8049 (shared-machine convention).' }
if (& $Runtime ps -a --format '{{.Names}}' | Where-Object { $_ -eq $container }) { throw "$container already exists; inspect and remove it first." }
if (-not (Get-Command surrealforge -ErrorAction SilentlyContinue)) { throw 'Install SurrealForge.Cli at the version AZOA.WebAPI.csproj references (dotnet tool install --global SurrealForge.Cli --version <v>).' }

$schemas = Join-Path ([IO.Path]::GetTempPath()) "azoa-p2-schemas-$PID"
New-Item -ItemType Directory -Force $schemas | Out-Null
try {
    Copy-Item (Join-Path $root 'Persistence\SurrealDb\Generated\Schemas\*.surql') $schemas
    $baselines = Join-Path $root 'Persistence\SurrealDb\CompatibilityBaselines'
    if (Test-Path $baselines) { Copy-Item (Join-Path $baselines '*.surql') $schemas }

    & $Runtime run -d --name $container --user 0:0 -p "127.0.0.1:${Port}:8000" $Image `
        start --user root --pass root --bind 0.0.0.0:8000 rocksdb:///data/db | Out-Null
    $endpoint = "http://127.0.0.1:$Port"
    for ($attempt = 0; -not (Test-NetConnection 127.0.0.1 -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue); $attempt++) {
        if ($attempt -gt 30) { throw 'Sandbox SurrealDB did not start.' }
        Start-Sleep -Seconds 1
    }

    function Invoke-DeployPath([string] $appliedBy) {
        $output = & surrealforge up --connection $endpoint --user root --pass root --namespace azoa --database azoa `
            --schemas-dir $schemas --migrations-dir (Join-Path $root 'Persistence\SurrealDb\Migrations') `
            --no-reconcile --applied-by $appliedBy 2>&1
        if ($LASTEXITCODE -ne 0) { $output | Write-Host; throw "surrealforge up failed ($appliedBy)." }
        return $output
    }
    Invoke-DeployPath 'p2-rehearsal' | Write-Host
    $summary = (Invoke-DeployPath 'p2-rehearsal-rerun') | Where-Object { $_ -match '^applied ' } | Select-Object -Last 1
    if ($summary -notmatch '^applied 0\b' -or $summary -notmatch 'evolved 0\b') { throw "Deploy path is not idempotent: $summary" }

    $env:AZOA_REHEARSAL_ENDPOINT = $endpoint
    $env:AZOA_REHEARSAL_NAMESPACE = 'azoa'
    $env:AZOA_REHEARSAL_DATABASE = 'azoa'
    dotnet test (Join-Path $root 'tests\AZOA.WebAPI.IntegrationTests\AZOA.WebAPI.IntegrationTests.csproj') -c Debug `
        --filter 'FullyQualifiedName~GeneratedSchemaDataCompatibilityTests'
    if ($LASTEXITCODE -ne 0) { throw 'Data-compatibility rehearsal failed; see the test output.' }
}
finally {
    Remove-Item Env:AZOA_REHEARSAL_ENDPOINT, Env:AZOA_REHEARSAL_NAMESPACE, Env:AZOA_REHEARSAL_DATABASE -ErrorAction SilentlyContinue
    & $Runtime rm -f $container 2>$null | Out-Null
    Remove-Item -Recurse -Force $schemas -ErrorAction SilentlyContinue
}
