<#
.SYNOPSIS
    Apply SurrealDB schema definitions (.surql files) to a running SurrealDB instance.
    Intended for local dev setup and CI; NOT for app boot (G5: schema via gated job).

.DESCRIPTION
    - Reads .surql files from Persistence/SurrealDb/Schemas/ in lexical order.
    - Executes each file against the target SurrealDB instance via the HTTP API.
    - Gracefully skips if the schema directory doesn't exist (Worker C not yet landed).
    - Tests tagged [Trait("Category","SurrealDbFull")] call this implicitly via
      IntegrationTestBase.InitializeAsync → ApplySchemasIfPresentAsync.
    - This script is a standalone runner for manual use and CI pre-flight.

.PARAMETER Endpoint
    SurrealDB HTTP endpoint. Default: http://localhost:8442

.PARAMETER Namespace
    Target namespace. Default: oasis

.PARAMETER Database
    Target database. Default: main

.PARAMETER Username
    SurrealDB root username. Default: root

.PARAMETER Password
    SurrealDB root password. Default: oasis-surreal-root

.PARAMETER SchemaDir
    Directory containing .surql schema files.
    Default: <repo-root>/Persistence/SurrealDb/Schemas/

.EXAMPLE
    # Apply all schemas to the local test container
    pwsh scripts/surrealdb/apply-schemas.ps1

    # Apply to a specific namespace/database
    pwsh scripts/surrealdb/apply-schemas.ps1 -Namespace oasis_dev -Database main

    # Apply to CI container
    pwsh scripts/surrealdb/apply-schemas.ps1 -Endpoint http://ci-surreal:8442 -Password $env:SURREAL_PASS
#>
[CmdletBinding()]
param(
    [string]$Endpoint  = ($env:OASIS_SURREAL_TEST_URL ?? "http://localhost:8442"),
    [string]$Namespace = "oasis",
    [string]$Database  = "main",
    [string]$Username  = ($env:OASIS_SURREAL_TEST_USER ?? "root"),
    [string]$Password  = ($env:OASIS_SURREAL_TEST_PASS ?? "oasis-surreal-root"),
    [string]$SchemaDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve schema directory ──────────────────────────────────────────────────

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)

if (-not $SchemaDir) {
    $SchemaDir = Join-Path $RepoRoot "Persistence" "SurrealDb" "Schemas"
}

if (-not (Test-Path $SchemaDir)) {
    Write-Warning "[apply-schemas] Schema directory not found: $SchemaDir"
    Write-Warning "[apply-schemas] Worker C's schema files have not landed yet. Skipping."
    exit 0
}

$surqlFiles = Get-ChildItem -Path $SchemaDir -Filter "*.surql" | Sort-Object Name

if ($surqlFiles.Count -eq 0) {
    Write-Warning "[apply-schemas] No .surql files found in $SchemaDir. Skipping."
    exit 0
}

Write-Host "[apply-schemas] Found $($surqlFiles.Count) schema file(s) in $SchemaDir"

# ── Verify container health ───────────────────────────────────────────────────

try {
    $health = Invoke-WebRequest -Uri "$Endpoint/health" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
    if ($health.StatusCode -ne 200) {
        Write-Error "[apply-schemas] SurrealDB health check failed (status $($health.StatusCode)). Is the container running? Run: pwsh scripts/surrealdb/start-test-container.ps1"
        exit 1
    }
} catch {
    Write-Error "[apply-schemas] Cannot reach SurrealDB at $Endpoint. Run: pwsh scripts/surrealdb/start-test-container.ps1"
    exit 1
}

Write-Host "[apply-schemas] SurrealDB is healthy at $Endpoint"

# ── Build auth header ─────────────────────────────────────────────────────────

$credentials = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes("${Username}:${Password}"))
$headers = @{
    "Authorization" = "Basic $credentials"
    "NS"            = $Namespace
    "DB"            = $Database
    "Accept"        = "application/json"
    "Content-Type"  = "text/plain"
}

# ── Apply each schema file ────────────────────────────────────────────────────

$errors = 0

foreach ($file in $surqlFiles) {
    Write-Host "[apply-schemas] Applying: $($file.Name) ..."
    $sql = Get-Content $file.FullName -Raw

    try {
        $response = Invoke-RestMethod `
            -Uri "$Endpoint/sql" `
            -Method Post `
            -Headers $headers `
            -Body $sql `
            -ContentType "text/plain" `
            -ErrorAction Stop

        # SurrealDB returns a JSON array; each element has a "status" field
        $failed = @($response | Where-Object { $_.status -ne "OK" })
        if ($failed.Count -gt 0) {
            Write-Warning "[apply-schemas] $($file.Name): $($failed.Count) statement(s) returned non-OK status:"
            $failed | ForEach-Object { Write-Warning "  $($_.detail ?? $_.result)" }
            $errors++
        } else {
            Write-Host "[apply-schemas]   OK ($($response.Count) statement(s))"
        }
    } catch {
        Write-Error "[apply-schemas] Failed to apply $($file.Name): $_"
        $errors++
    }
}

# ── Result ────────────────────────────────────────────────────────────────────

if ($errors -gt 0) {
    Write-Error "[apply-schemas] $errors schema file(s) had errors. Review output above."
    exit 1
}

Write-Host "[apply-schemas] All $($surqlFiles.Count) schema file(s) applied successfully to $Namespace/$Database."
exit 0
