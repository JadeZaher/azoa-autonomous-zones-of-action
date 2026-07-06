<#
.SYNOPSIS
    Shared container-runtime auto-detect for backup.ps1 / restore.ps1.

.DESCRIPTION
    Mirrors the Find-Compose detection idiom in dev-down.ps1: prefer `docker`,
    fall back to `podman`, throw if neither responds. See AGENTS.md.
#>
function Find-ContainerRuntime {
    [CmdletBinding()]
    param()

    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $null = docker version 2>&1
        if ($LASTEXITCODE -eq 0) { return 'docker' }
    }

    if (Get-Command podman -ErrorAction SilentlyContinue) {
        $null = podman version 2>&1
        if ($LASTEXITCODE -eq 0) { return 'podman' }
    }

    throw "Find-ContainerRuntime: neither 'docker' nor 'podman' responded on this host. Install one, or start it, and retry."
}
