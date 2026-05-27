<#
.SYNOPSIS
    Regenerate docs/aggregates/*.mermaid + docs/domain.generated.mermaid
    from Persistence/SurrealDb/Schemas/source/*.mermaid (RUNBOOK §4 Phase B).

.DESCRIPTION
    Thin wrapper over `oasis-surreal aggregates`. Use after editing a
    source .mermaid file to refresh the visualization layer.

    The slice files + master diagram are checked into git so GitHub
    renders the master inline on the repo landing page (configured via
    README.md mermaid block referencing docs/domain.generated.mermaid).

    Authors edit source/*.mermaid; readers consume docs/*.mermaid.

.EXAMPLE
    PS> ./scripts/regen-aggregates.ps1
    wrote 6 slice file(s) to docs/aggregates
      - bridge.mermaid
      - dapp_composition.mermaid
      - identity.mermaid
      - quest.mermaid
      - quest_templates.mermaid
      - wallet_nft.mermaid
    wrote master diagram to docs/domain.generated.mermaid
#>
[CmdletBinding()]
param(
    [string] $SourceDir = "Persistence/SurrealDb/Schemas/source",
    [string] $OutDir    = "docs"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$proj = "packages/Oasis.SurrealDb.Schema/Oasis.SurrealDb.Schema.csproj"
& dotnet run --project $proj --framework net8.0 -- `
    aggregates --source $SourceDir --out $OutDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "aggregates emit failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
