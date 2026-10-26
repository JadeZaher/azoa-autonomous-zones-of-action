<#
.SYNOPSIS
    Idempotent local AZOA node for the tandem demo: SurrealDB + dev KYC sidecar + WebAPI (host-run),
    tenant key mint-or-reuse, and .omc/state/dev-node-env.json. PowerShell 5.1-compatible.

.DESCRIPTION
    See tools/dev-node/AGENTS.md. Re-running on a live stack is a no-op that prints status.
    Tear down with tools/dev-node/dev-node-down.ps1 (stops only what this script recorded).

.EXAMPLE
    ./tools/dev-node/dev-node-up.ps1
    ./tools/dev-node/dev-node-up.ps1 -KycMode manual -StartPodmanMachine
#>
[CmdletBinding()]
param(
    [int]$NodePort = 5000,
    [int]$SurrealPort = 8020,
    [int]$SidecarApiPort = 8031,
    [int]$SidecarWidgetPort = 8032,
    [ValidateSet('approve-on-begin', 'approve-on-poll', 'manual')]
    [string]$KycMode = 'approve-on-begin',
    [int]$HealthTimeoutSeconds = 240,
    [switch]$StartPodmanMachine,
    [switch]$SkipKybBootstrap
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dev-node-common.ps1')

$NodeBaseUrl       = "http://127.0.0.1:$NodePort"
$SidecarApiUrl     = "http://127.0.0.1:$SidecarApiPort"
$SidecarWidgetUrl  = "http://127.0.0.1:$SidecarWidgetPort"
$ComposeFile       = Join-Path $RepoRoot 'docker-compose.dev.yml'
$WebApiDll         = Join-Path $RepoRoot 'bin\Debug\net10.0\AZOA.WebAPI.dll'
$TenantKeyScopes   = 'tenant:provision,wallet:manage,kyc:read,kyc:submit'
# swap:sign: the W5.1 reserve exchange door (ExchangeController) refuses without it.
# transfer:sign: reserve-funded award HOLDS sign under it (ActionSigningScopes.cs:15) -- the
# TENANT's own credential, never a custodial child grant.
$ValueKeyScopes    = 'wallet:manage,kyc:read,kyc:submit,units:issue,nft:mint,token:create:sign,grant:sign,swap:sign,transfer:sign'
$ArdaGenesisChain   = 'Simulated'
$ArdaGenesisNetwork = 'Devnet'
$ArdaOnRampPartition = 'on-ramp'
$ArdaOnRampBudget   = '1000000000'
$StartedThisRun    = New-Object System.Collections.ArrayList

# ── Secrets: generated once per machine, stable across restarts (PersonKeySecret must not rotate) ──
function Get-DevSecrets {
    $secrets = Read-JsonFile $SecretFile
    if ($null -ne $secrets) { return $secrets }
    $secrets = [pscustomobject]@{
        companyKey      = 'dev-company-' + (New-RandomHex 16)
        kycConfigId     = 'dev-kyc-config'
        kybConfigId     = 'dev-kyb-config'
        callbackApiKey  = New-RandomHex 24
        personKeySecret = New-RandomHex 24
    }
    Write-JsonFile -Path $SecretFile -Value $secrets
    Write-Step "Generated dev vendor secrets -> $SecretFile"
    return $secrets
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds, [System.Diagnostics.Process]$Process = $null, [string]$What)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        if ($null -ne $Process -and $Process.HasExited) { Fail "$What exited (code $($Process.ExitCode)) before becoming healthy." }
        Start-Sleep -Milliseconds 1000
    }
    return $false
}

function Start-Recorded {
    param([string]$Name, [string]$FilePath, [string[]]$Arguments, [hashtable]$Environment)
    $stdout = Join-Path $LogDir "$Name.log"
    $stderr = Join-Path $LogDir "$Name.err.log"
    $process = Invoke-WithEnvironment -Variables $Environment -Action {
        Start-Process -FilePath $FilePath -ArgumentList $Arguments -WorkingDirectory $RepoRoot `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    }
    $recorded = Get-RecordedPids
    Set-RecordedEntry $recorded $Name ([pscustomobject]@{ pid = $process.Id; startedAt = (Get-ProcessStartUtc $process); log = $stdout })
    Save-RecordedPids $recorded
    [void]$StartedThisRun.Add($Name)
    Write-Step "$Name started (pid $($process.Id)); logs: $stdout"
    return $process
}

# ── 1. SurrealDB ───────────────────────────────────────────────────────────────
function Ensure-SurrealDb {
    $healthUrl = "http://127.0.0.1:$SurrealPort/health"
    $recorded = Get-RecordedPids
    if (Test-HttpOk $healthUrl) {
        if ($null -ne (Get-Field $recorded 'surrealdb')) { Write-Step "SurrealDB already up on :$SurrealPort (started by dev-node-up)." }
        else { Write-Step "SurrealDB already answering on :$SurrealPort (not started by dev-node-up; down will leave it running)." }
        return $SurrealPort
    }

    $runtime = Find-ContainerRuntime
    if ($runtime.ContainsKey('Error') -and $StartPodmanMachine -and (Get-Command podman -ErrorAction SilentlyContinue)) {
        Write-Step 'Starting the podman machine (-StartPodmanMachine) ...'
        $started = Invoke-Native 'podman' @('machine', 'start')
        if ($started.Code -ne 0) { Write-Warn $started.Output }
        $runtime = Find-ContainerRuntime
    }
    if ($runtime.ContainsKey('Error')) {
        Fail ("No running container runtime for SurrealDB: $($runtime.Error). " +
            "Start Docker Desktop or the podman machine (or re-run with -StartPodmanMachine), or run any SurrealDB v3 on 127.0.0.1:$SurrealPort.")
    }

    $existingPort = Get-SurrealContainerHostPort $runtime.Engine
    if ($null -ne $existingPort -and $existingPort -ne $SurrealPort) {
        Fail ("Container $SurrealContainer is already running on host port $existingPort (likely ./dev-up.ps1, which defaults to 8000). " +
            "Re-run with -SurrealPort $existingPort to share it, or stop it first.")
    }

    $head = Invoke-Native 'git' @('-C', $RepoRoot, 'rev-parse', 'HEAD')
    Write-Step "Starting SurrealDB via $($runtime.ComposeExe) $($runtime.ComposePreArgs -join ' ') (host port $SurrealPort) ..."
    $arguments = @() + $runtime.ComposePreArgs + @('-f', $ComposeFile, 'up', '-d', 'surrealdb')
    $up = Invoke-WithEnvironment -Variables @{ SURREALDB_HOST_PORT = "$SurrealPort"; SOURCE_REVISION = $head.Output.Trim() } -Action {
        Invoke-Native $runtime.ComposeExe $arguments
    }
    if ($up.Code -ne 0) { Fail "compose up surrealdb failed:`n$($up.Output)" }

    $recorded = Get-RecordedPids
    Set-RecordedEntry $recorded 'surrealdb' ([pscustomobject]@{ engine = $runtime.Engine; container = $SurrealContainer; port = $SurrealPort; startedAt = (Get-Date).ToUniversalTime().ToString('o') })
    Save-RecordedPids $recorded
    [void]$StartedThisRun.Add('surrealdb')

    if (-not (Wait-Until -Condition { Test-HttpOk $healthUrl } -TimeoutSeconds 90 -What 'SurrealDB')) {
        Fail "SurrealDB did not become healthy on $healthUrl within 90s."
    }
    Write-Step "SurrealDB healthy on :$SurrealPort."
    return $SurrealPort
}

# ── 2. Schema (idempotent `surrealforge up`, same flags as docker-entrypoint.sh) ──
function Get-SchemaCli {
    $csproj = Get-Content -Raw (Join-Path $RepoRoot 'AZOA.WebAPI.csproj')
    $match = [regex]::Match($csproj, 'Include="SurrealForge\.Schema"\s+Version="([^"]+)"')
    if (-not $match.Success) { Fail 'Could not read the SurrealForge.Schema version from AZOA.WebAPI.csproj.' }
    $version = $match.Groups[1].Value
    $toolDir = Join-Path $RepoRoot ".omc\tools\dev-node-schema-cli\$version"
    $exe = Join-Path $toolDir 'surrealforge.exe'
    if (Test-Path $exe) { return $exe }
    Write-Step "Installing SurrealForge.Cli $version into $toolDir ..."
    $install = Invoke-Native 'dotnet' @('tool', 'install', '--tool-path', $toolDir, 'SurrealForge.Cli', '--version', $version)
    if ($install.Code -eq 0 -and (Test-Path $exe)) { return $exe }
    $fallback = Join-Path $RepoRoot '.omc\tools\surrealforge.exe'
    if (Test-Path $fallback) {
        Write-Warn "SurrealForge.Cli $version install failed; falling back to $fallback.`n$($install.Output)"
        return $fallback
    }
    Fail "SurrealForge.Cli $version is unavailable:`n$($install.Output)"
}

function Invoke-SchemaUp {
    param([int]$Port)
    $cli = Get-SchemaCli
    $schemaDir = Join-Path $StateDir 'schemas'
    if (Test-Path $schemaDir) { Remove-Item -Recurse -Force $schemaDir }
    New-Item -ItemType Directory -Path $schemaDir | Out-Null
    Copy-Item (Join-Path $RepoRoot 'Persistence\SurrealDb\Generated\Schemas\*.surql') $schemaDir
    $baselines = Join-Path $RepoRoot 'Persistence\SurrealDb\CompatibilityBaselines'
    if (Test-Path $baselines) { Copy-Item (Join-Path $baselines '*.surql') $schemaDir }
    Write-Step 'Applying schemas + migrations (surrealforge up --no-reconcile) ...'
    $result = Invoke-Native $cli @('up', '--connection', "http://127.0.0.1:$Port", '--user', 'root', '--pass', 'root',
        '--namespace', 'azoa', '--database', 'azoa', '--schemas-dir', $schemaDir,
        '--migrations-dir', (Join-Path $RepoRoot 'Persistence\SurrealDb\Migrations'),
        '--no-reconcile', '--applied-by', 'azoa-local/dev-node-up')
    if ($result.Code -ne 0) { Fail "surrealforge up failed:`n$($result.Output)" }
    Write-Step 'Schema in sync.'
}

# ── 3. KYC sidecar ─────────────────────────────────────────────────────────────
function Ensure-Sidecar {
    param($Secrets)
    $healthUrl = "$SidecarApiUrl/__dev/health"
    $owned = Get-OwnedProcess (Get-Field (Get-RecordedPids) 'sidecar')
    if ($null -ne $owned -and (Test-HttpOk $healthUrl)) { Write-Step "KYC sidecar already up (pid $($owned.Id)) on $SidecarApiUrl / $SidecarWidgetUrl."; return }
    if ($null -eq $owned -and (Test-HttpOk $healthUrl)) { Fail "Port $SidecarApiPort already serves a sidecar this script did not start; stop it or pass -SidecarApiPort." }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail 'node (>= 20) is required for the KYC sidecar.' }
    $process = Start-Recorded -Name 'sidecar' -FilePath 'node' -Arguments @('tools/dev-kyc-vendor/server.mjs') -Environment @{
        DEV_KYC_HOST = '127.0.0.1'; DEV_KYC_API_PORT = "$SidecarApiPort"; DEV_KYC_WIDGET_PORT = "$SidecarWidgetPort"
        DEV_KYC_COMPANY_KEY = $Secrets.companyKey; DEV_KYC_KYC_CONFIG_ID = $Secrets.kycConfigId; DEV_KYC_KYB_CONFIG_ID = $Secrets.kybConfigId
        DEV_KYC_CALLBACK_API_KEY = $Secrets.callbackApiKey; DEV_KYC_NODE_BASE_URL = $NodeBaseUrl; DEV_KYC_MODE = $KycMode
    }
    if (-not (Wait-Until -Condition { Test-HttpOk $healthUrl } -TimeoutSeconds 20 -Process $process -What 'KYC sidecar')) {
        Show-LogTail (Join-Path $LogDir 'sidecar.err.log')
        Fail "KYC sidecar did not answer $healthUrl within 20s."
    }
    Write-Step "KYC sidecar healthy (mode $KycMode)."
}

# ── 4. WebAPI (built, then run as a single tracked dotnet process) ─────────────
function Ensure-WebApi {
    param($Secrets, [int]$Port, [ref]$AlreadyRunning)
    $healthUrl = "$NodeBaseUrl/health"
    $owned = Get-OwnedProcess (Get-Field (Get-RecordedPids) 'webapi')
    if ($null -ne $owned -and (Test-HttpOk $healthUrl 5)) { Write-Step "WebAPI already up (pid $($owned.Id)) on $NodeBaseUrl."; $AlreadyRunning.Value = $true; return }
    if ($null -eq $owned -and (Test-HttpOk $healthUrl 5)) {
        Fail "$NodeBaseUrl is already serving a node this script did not start (./dev-up.ps1's azoa-dev-api container?). Stop it or pass -NodePort."
    }
    $AlreadyRunning.Value = $false
    Invoke-SchemaUp -Port $Port

    Write-Step 'Building AZOA.WebAPI (Debug) ...'
    $build = Invoke-Native 'dotnet' @('build', (Join-Path $RepoRoot 'AZOA.WebAPI.csproj'), '-c', 'Debug', '-nologo', '-v', 'quiet')
    if ($build.Code -ne 0) { Fail "dotnet build failed:`n$($build.Output)" }
    if (-not (Test-Path $WebApiDll)) { Fail "Build succeeded but $WebApiDll is missing." }

    $process = Start-Recorded -Name 'webapi' -FilePath 'dotnet' -Arguments @("`"$WebApiDll`"") -Environment @{
        ASPNETCORE_ENVIRONMENT = 'Development'
        ASPNETCORE_URLS = $NodeBaseUrl
        SurrealRuntime__Endpoint = "http://127.0.0.1:$Port"
        Blockchain__Mode = 'Simulated'
        CustodialAccounts__CustodyMode = 'DevelopmentOnly'
        Kyc__Provider = 'identomat'
        Kyc__Identomat__BeginBaseUrl = $SidecarApiUrl
        Kyc__Identomat__ResultBaseUrl = $SidecarWidgetUrl
        Kyc__Identomat__CompanyKey = $Secrets.companyKey
        Kyc__Identomat__ConfigId = $Secrets.kycConfigId
        Kyc__Identomat__CallbackApiKey = $Secrets.callbackApiKey
        Kyc__Identomat__AllowInsecureDevVendor = 'true'
        Kyc__Identity__PersonKeySecret = $Secrets.personKeySecret
        Kyb__ConfigId = $Secrets.kybConfigId
    }
    Write-Step "Waiting up to ${HealthTimeoutSeconds}s for $healthUrl ..."
    if (-not (Wait-Until -Condition { Test-HttpOk $healthUrl 5 } -TimeoutSeconds $HealthTimeoutSeconds -Process $process -What 'WebAPI')) {
        $last = Invoke-Api -Method 'GET' -Url $healthUrl -TimeoutSec 5
        Write-Warn "Last /health answer: HTTP $($last.Status) $($last.Raw)"
        Show-LogTail (Join-Path $LogDir 'webapi.log')
        Show-LogTail (Join-Path $LogDir 'webapi.err.log')
        Fail "WebAPI did not become healthy within ${HealthTimeoutSeconds}s."
    }
    Write-Step 'WebAPI healthy.'
}

# ── 5. Tenant: mint-or-reuse keys, assign the Identomat profile ───────────────
function Test-ApiKey {
    param([string]$Key, [string]$TenantId)
    if ([string]::IsNullOrWhiteSpace($Key) -or [string]::IsNullOrWhiteSpace($TenantId)) { return $false }
    $probe = Invoke-Api -Method 'GET' -Url "$NodeBaseUrl/api/user/$TenantId" -Headers @{ 'X-Api-Key' = $Key }
    return ($probe.Status -eq 200)
}

function Get-OperatorToken {
    $settings = Get-Content -Raw (Join-Path $RepoRoot 'appsettings.Development.json') | ConvertFrom-Json
    $login = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/operator/session" -Body @{
        username = $settings.NodeOperator.Username; password = $settings.NodeOperator.Password }
    $token = Get-Field (Get-Field $login.Body 'result') 'accessToken'
    if ($login.Status -ne 200 -or [string]::IsNullOrWhiteSpace($token)) { Fail "Node operator login failed: HTTP $($login.Status) $($login.Raw)" }
    return $token
}

function Get-TenantJwt {
    param($Tenant)
    # UserLoginValidator requires Email even though UserLoginModel offers UserId (node-side
    # mismatch, recorded in the track); email login is the path that actually validates.
    $login = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/user/login" -Body @{ email = $Tenant.email; password = $Tenant.password }
    $jwt = Get-Field $login.Body 'result'
    if ($login.Status -ne 200 -or [string]::IsNullOrWhiteSpace($jwt)) { return $null }
    return $jwt
}

function Ensure-Tenant {
    $tenant = Read-JsonFile $TenantFile
    if ($null -ne $tenant -and (Test-ApiKey $tenant.tenantApiKey $tenant.tenantId) -and (Test-ApiKey $tenant.tenantValueApiKey $tenant.tenantId)) {
        Write-Step "Reusing tenant $($tenant.tenantId) and its two API keys (both validated)."
        return $tenant
    }

    # A stored tenant whose login still works keeps its id (ArdaNova binds Azoa:TenantId to it); otherwise
    # (first run, or the SurrealDB volume was wiped) a fresh tenant user is registered.
    $jwt = $null
    if ($null -ne $tenant -and -not [string]::IsNullOrWhiteSpace((Get-Field $tenant 'tenantId'))) {
        $jwt = Get-TenantJwt $tenant
        if ($null -ne $jwt) { Write-Step "Re-minting keys for existing tenant $($tenant.tenantId) (a stored key no longer validated)." }
        else { Write-Warn "Stored tenant $($tenant.tenantId) can no longer sign in (DB reset?); registering a new tenant." }
    }
    if ($null -eq $jwt) {
        $suffix = New-RandomHex 4
        $email = "ardanova-dev-tenant-$suffix@dev.azoa.local"
        $password = 'DevTenant-' + (New-RandomHex 16) + '!'
        $registered = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/user/register" -Body @{
            username = "ardanova-dev-$suffix"; email = $email; password = $password; firstName = 'ArdaNova'; lastName = 'DevTenant' }
        $id = Get-Field (Get-Field $registered.Body 'result') 'id'
        if ($registered.Status -ne 200 -or [string]::IsNullOrWhiteSpace($id)) { Fail "Tenant user registration failed: HTTP $($registered.Status) $($registered.Raw)" }
        $tenant = [pscustomobject]@{ tenantId = $id; email = $email; password = $password; tenantApiKey = $null; tenantValueApiKey = $null }
        Write-JsonFile -Path $TenantFile -Value $tenant
        Write-Step "Registered tenant user $id."
        $jwt = Get-TenantJwt $tenant
        if ($null -eq $jwt) { Fail "The freshly registered tenant $id could not sign in." }
    }

    $operatorHeaders = @{ Authorization = 'Bearer ' + (Get-OperatorToken) }
    $issued = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/apikey/tenant" -Headers $operatorHeaders -Body @{
        tenantUserId = $tenant.tenantId; name = 'ardanova-dev-tenant'; expiresInDays = 365 }
    $tenantKey = Get-Field (Get-Field $issued.Body 'result') 'key'
    if ($issued.Status -ne 200 -or [string]::IsNullOrWhiteSpace($tenantKey)) { Fail "Operator tenant-key issuance failed: HTTP $($issued.Status) $($issued.Raw)" }

    $valueIssued = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/apikey" -Headers @{ Authorization = "Bearer $jwt" } -Body @{
        name = 'ardanova-dev-tenant-value'; scopes = $ValueKeyScopes; expiresInDays = 365 }
    $valueKey = Get-Field (Get-Field $valueIssued.Body 'result') 'key'
    if ($valueIssued.Status -ne 200 -or [string]::IsNullOrWhiteSpace($valueKey)) { Fail "Value-scope key self-issuance failed: HTTP $($valueIssued.Status) $($valueIssued.Raw)" }

    $tenant.tenantApiKey = $tenantKey
    $tenant.tenantValueApiKey = $valueKey
    Write-JsonFile -Path $TenantFile -Value $tenant
    Write-Step 'Minted tenant keys (provisioning + value scopes).'

    # Operator enables the Identomat profile and selects it for this tenant (same calls as HttpFlowTestBase).
    $profile = Invoke-Api -Method 'PUT' -Url "$NodeBaseUrl/api/operator/kyc/providers/identomat" -Headers $operatorHeaders -Body @{
        displayName = 'Identomat'; adapterKey = 'identomat'; enabled = $true
        policyVersion = 'identomat-hosted-v1'; assuranceLevel = 'document-and-liveness' }
    if ($profile.Status -ne 200 -and $profile.Status -ne 409) { Fail "Enabling the Identomat profile failed: HTTP $($profile.Status) $($profile.Raw)" }
    $selection = Invoke-Api -Method 'PUT' -Url "$NodeBaseUrl/api/operator/tenants/$($tenant.tenantId)/kyc-provider" -Headers $operatorHeaders -Body @{ providerKey = 'identomat' }
    # 409 = already selected; idempotent re-runs land here.
    if ($selection.Status -ne 200 -and $selection.Status -ne 409) { Fail "Selecting Identomat for the tenant failed: HTTP $($selection.Status) $($selection.Raw)" }
    Write-Step 'Identomat profile enabled and assigned to the tenant.'
    return $tenant
}

# ── 6. Tenant KYB (custodial accounts require it, D-6) — best effort ──────────
function Ensure-TenantKyb {
    param($Tenant)
    $headers = @{ 'X-Api-Key' = $Tenant.tenantApiKey }
    $status = Invoke-Api -Method 'GET' -Url "$NodeBaseUrl/api/tenant/business-verification" -Headers $headers
    $current = Get-Field (Get-Field $status.Body 'result') 'status'
    if ($current -eq 'APPROVED') { Write-Step 'Tenant KYB already APPROVED.'; return $current }
    $headers['Idempotency-Key'] = 'dev-node-kyb-' + $Tenant.tenantId
    $begun = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/tenant/business-verification/session" -Headers $headers
    if ($begun.Status -ne 200) { Write-Warn "Tenant KYB begin answered HTTP $($begun.Status): $($begun.Raw)"; return $current }
    $headers.Remove('Idempotency-Key')
    if ($KycMode -eq 'manual') {
        Write-Warn "KYC mode is manual: set the tenant KYB verdict via POST $SidecarApiUrl/__dev/sessions/<id>/verdict."
        return 'PENDING'
    }
    $approved = Wait-Until -TimeoutSeconds 30 -What 'KYB' -Condition {
        $poll = Invoke-Api -Method 'GET' -Url "$NodeBaseUrl/api/tenant/business-verification" -Headers $headers
        (Get-Field (Get-Field $poll.Body 'result') 'status') -eq 'APPROVED'
    }
    if ($approved) { Write-Step 'Tenant KYB APPROVED via the sidecar.'; return 'APPROVED' }
    Write-Warn 'Tenant KYB did not reach APPROVED within 30s; custodial enrolment will refuse until it does.'
    return 'PENDING'
}

# ── 6b. Tenant first-party KYC — value endpoints gate on RequireValueAccessAsync(actor),
# so the tenant user needs its own KYC approval, not just KYB (found live: fungible-mint
# answered KYC_FORBIDDEN with KYB APPROVED). Best effort, same shape as KYB above.
function Ensure-TenantKyc {
    param($Tenant)
    $jwt = Get-TenantJwt $Tenant
    if ($null -eq $jwt) { Write-Warn 'Tenant KYC skipped: tenant cannot sign in.'; return 'UNKNOWN' }
    $headers = @{ Authorization = "Bearer $jwt" }
    $status = Invoke-Api -Method 'GET' -Url "$NodeBaseUrl/api/kyc/status" -Headers $headers
    $current = Get-Field (Get-Field $status.Body 'result') 'status'
    if ($current -eq 'APPROVED') { Write-Step 'Tenant KYC already APPROVED.'; return $current }
    $begun = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/kyc/session" -Headers $headers -Body @{}
    if ($begun.Status -ne 200) { Write-Warn "Tenant KYC begin answered HTTP $($begun.Status): $($begun.Raw)"; return $current }
    if ($KycMode -eq 'manual') {
        Write-Warn "KYC mode is manual: set the tenant KYC verdict via POST $SidecarApiUrl/__dev/sessions/<id>/verdict."
        return 'PENDING'
    }
    $approved = Wait-Until -TimeoutSeconds 45 -What 'tenant KYC' -Condition {
        $poll = Invoke-Api -Method 'GET' -Url "$NodeBaseUrl/api/kyc/status" -Headers $headers
        (Get-Field (Get-Field $poll.Body 'result') 'status') -eq 'APPROVED'
    }
    if ($approved) { Write-Step 'Tenant KYC APPROVED via the sidecar.'; return 'APPROVED' }
    Write-Warn 'Tenant KYC did not reach APPROVED within 45s; value endpoints (fungible-mint, exchange) will refuse KYC_FORBIDDEN until it does.'
    return 'PENDING'
}

# ── 7. ARDA platform-unit genesis on the Simulated chain (see AGENTS.md §arda-genesis) ──
function Get-PlatformTokenDefinition {
    param([hashtable]$Headers)
    $state = Invoke-Api -Method 'GET' -Url "$NodeBaseUrl/api/admin/platform-token" -Headers $Headers
    $definition = Get-Field (Get-Field $state.Body 'result') 'definition'
    if ($state.Status -eq 200 -and -not [string]::IsNullOrWhiteSpace((Get-Field $definition 'assetId'))) { return $definition }
    return $null
}

function Ensure-ArdaGenesis {
    $headers = @{ Authorization = 'Bearer ' + (Get-OperatorToken) }
    $definition = Get-PlatformTokenDefinition $headers
    if ($null -ne $definition) {
        Write-Step "ARDA already genesised: asset $($definition.assetId) on $($definition.chainType)/$($definition.network)."
        return $definition
    }

    # Empty definition fields = PlatformTokenGenesisDefaults (Arda/ARDA, 2 decimals, 2_000_000_000 base units).
    $genesis = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/admin/platform-token/genesis" -Headers $headers -Body @{
        chainType = $ArdaGenesisChain; expectedNetwork = $ArdaGenesisNetwork; confirmIrreversible = $true }
    if ($genesis.Status -ne 200 -and $genesis.Status -ne 202) { Fail "ARDA genesis was refused: HTTP $($genesis.Status) $($genesis.Raw)" }
    $operation = Get-Field (Get-Field $genesis.Body 'result') 'operation'

    # Drive the durable creation through explicit operator cost consent: quote -> accept -> continue -> refresh.
    for ($step = 0; $null -ne $operation -and $operation.state -ne 'Completed' -and $step -lt 12; $step++) {
        $base = "$NodeBaseUrl/api/admin/platform-token/creation/$($operation.operationId)"
        switch ($operation.state) {
            'AwaitingConsent' {
                if ($null -eq $operation.quote) { $next = Invoke-Api -Method 'POST' -Url "$base/quote" -Headers $headers }
                else { $next = Invoke-Api -Method 'POST' -Url "$base/accept-quote" -Headers $headers -Body @{ version = $operation.version; quoteId = $operation.quote.quoteId } }
            }
            'Admitted' { $next = Invoke-Api -Method 'POST' -Url "$base/continue" -Headers $headers -Body @{ requestId = [guid]::NewGuid().ToString(); version = $operation.version } }
            default { $next = Invoke-Api -Method 'POST' -Url "$base/refresh" -Headers $headers }
        }
        if ($next.Status -lt 200 -or $next.Status -ge 300) { Fail "ARDA genesis stalled in state $($operation.state): HTTP $($next.Status) $($next.Raw)" }
        $operation = Get-Field $next.Body 'result'
        if ($operation.state -in @('Cancelled', 'FailedNoEffect')) { Fail "ARDA genesis ended $($operation.state): $($next.Raw)" }
    }

    $definition = Get-PlatformTokenDefinition $headers
    if ($null -eq $definition) { Fail 'ARDA genesis did not reach a readable platform-token definition.' }
    Write-Step "ARDA genesised: asset $($definition.assetId) on $($definition.chainType)/$($definition.network) ($($definition.decimals) decimals, total $($definition.totalSupply) base units)."
    return $definition
}

# Re-declaring SETS the budget (never adds), so this is safe on every run.
function Ensure-ArdaOnRampPartition {
    $headers = @{ Authorization = 'Bearer ' + (Get-OperatorToken) }
    $declared = Invoke-Api -Method 'POST' -Url "$NodeBaseUrl/api/admin/platform-token/partitions" -Headers $headers -Body @{
        partitions = @{ $ArdaOnRampPartition = $ArdaOnRampBudget } }
    $partition = @(Get-Field (Get-Field $declared.Body 'result') 'partitions') | Where-Object { $_.name -eq $ArdaOnRampPartition } | Select-Object -First 1
    if ($declared.Status -ne 200 -or $null -eq $partition) { Fail "Declaring the ARDA '$ArdaOnRampPartition' partition failed: HTTP $($declared.Status) $($declared.Raw)" }
    Write-Step "ARDA '$ArdaOnRampPartition' partition declared at $($partition.declared) base units (available $($partition.available))."
    return [pscustomobject]@{ name = $ArdaOnRampPartition; declared = [string]$partition.declared; available = [string]$partition.available }
}

# ── Main ──────────────────────────────────────────────────────────────────────
try {
    Initialize-StateDir
    $secrets = Get-DevSecrets
    $surrealPortInUse = Ensure-SurrealDb
    Ensure-Sidecar -Secrets $secrets
    $wasRunning = $false
    Ensure-WebApi -Secrets $secrets -Port $surrealPortInUse -AlreadyRunning ([ref]$wasRunning)
    $tenant = Ensure-Tenant
    $kybStatus = 'skipped'
    if (-not $SkipKybBootstrap) { $kybStatus = Ensure-TenantKyb -Tenant $tenant }
    $tenantKycStatus = Ensure-TenantKyc -Tenant $tenant
    $arda = Ensure-ArdaGenesis
    $ardaOnRamp = Ensure-ArdaOnRampPartition

    Write-JsonFile -Path $EnvFile -Value ([pscustomobject]@{
        baseUrl         = $NodeBaseUrl
        tenantApiKey    = $tenant.tenantApiKey
        tenantId        = $tenant.tenantId
        kyc             = [pscustomobject]@{
            beginBaseUrl   = $SidecarApiUrl
            resultBaseUrl  = $SidecarWidgetUrl
            companyKey     = $secrets.companyKey
            configId       = $secrets.kycConfigId
            callbackApiKey = $secrets.callbackApiKey
        }
        surrealEndpoint = "http://127.0.0.1:$surrealPortInUse"
        tenantApiKeyScopes      = $TenantKeyScopes
        tenantValueApiKey       = $tenant.tenantValueApiKey
        tenantValueApiKeyScopes = $ValueKeyScopes
        kybStatus       = $kybStatus
        tenantKycStatus = $tenantKycStatus
        ardaAssetId     = $arda.assetId
        ardaChainType   = $arda.chainType
        ardaDecimals    = $arda.decimals
        ardaOnRampPartition = $ardaOnRamp
        sidecarControlUrl = "$SidecarApiUrl/__dev"
        writtenAt       = (Get-Date).ToUniversalTime().ToString('o')
    })
    if ($StartedThisRun.Count -eq 0) { Write-Step 'Stack was already live; nothing started (no-op).' }
    Write-Step "Wrote $EnvFile"
    Write-Step "Node $NodeBaseUrl | tenant $($tenant.tenantId) | KYC sidecar $SidecarApiUrl ($KycMode) | SurrealDB :$surrealPortInUse | KYB $kybStatus"
    Write-Step 'Tear down: ./tools/dev-node/dev-node-down.ps1'
    exit 0
} catch {
    if (-not $_.Exception.Message.StartsWith('dev-node:')) { Write-Host "[dev-node] FATAL: $($_.Exception.Message)" -ForegroundColor Red }
    if ($StartedThisRun.Count -gt 0) {
        Write-Step "Reaping what this run started: $($StartedThisRun -join ', ')"
        & (Join-Path $PSScriptRoot 'dev-node-down.ps1') -Only $StartedThisRun
    }
    exit 1
}
