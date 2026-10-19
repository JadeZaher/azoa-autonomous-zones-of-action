---
type: runbook
---

# Local tenant AI drill

The bounded model/service smoke was completed on 2026-10-18: the final strict-schema request made one real OpenRouter HTTP 200 call and produced a typed follow-up interview with no draft, persistence or execution. Three synthetic requests total were used across diagnosis and verification. The full authenticated browser drill below remains unexecuted. See `verification.md`; do not treat transport/service evidence as UI task completion.

Use the existing local API database/auth configuration and a real local test tenant. The feature defaults to disabled. This procedure adds only process-local AI configuration and never changes Railway or writes a credential into a file.

The operator must have issued an active `tenant:provision` credential for the same active account that logs into the tenant UI, and assigned that account the DApp developer/manager role. The assistant checks both. A normal developer account without tenant authority receives an honest setup message; a root/operator session is not a tenant authoring session. The model never receives the tenant id or login credential.

In the AI worktree, launch a second local API after the normal local database/auth settings have been supplied:

```powershell
$piConfigPath = Join-Path $env:USERPROFILE '.pi/agent/models.json'
$piConfig = Get-Content -LiteralPath $piConfigPath -Raw | ConvertFrom-Json
$piProvider = $piConfig.providers.'openrouter-free'
$piModel = $piProvider.models | Where-Object id -eq 'anthropic/claude-sonnet-4.6' | Select-Object -First 1
if (-not $piModel) { $piModel = $piProvider.models | Select-Object -First 1 }
if (-not $piProvider.apiKey -or -not $piModel.id) { throw 'Configured provider/model is missing.' }
$env:WorkflowDesigner__Enabled = 'true'
$env:WorkflowDesigner__ApiKey = $piProvider.apiKey
$env:WorkflowDesigner__Model = $piModel.id
$env:WorkflowDesigner__RequestsPerHour = '12'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5062'
try {
    dotnet run --project AZOA.WebAPI.csproj --no-build --no-launch-profile
} finally {
    Remove-Item Env:WorkflowDesigner__ApiKey -ErrorAction SilentlyContinue
    Remove-Item Env:WorkflowDesigner__Enabled -ErrorAction SilentlyContinue
    Remove-Item Env:WorkflowDesigner__Model -ErrorAction SilentlyContinue
    Remove-Item Env:WorkflowDesigner__RequestsPerHour -ErrorAction SilentlyContinue
}
```

Do not print `$piConfig`, `$piProvider`, environment dumps or the provider request/response bodies. The example assumes the authorized Pi configuration contains a literal OpenRouter key; do not execute a command-valued key source blindly. Provider-account spending limits remain necessary before any shared deployment; the API's tenant request budget is per instance, not a fleet-wide dollar cap.

In a separate terminal, launch the frontend against that local API:

```powershell
Set-Location frontend
$env:API_URL = 'http://localhost:5062'
npm.cmd run dev -- --port 3062
```

Log in with the real local tenant account. Open Actions → Builder → Design an economic workflow with AI. Select the server-verified tenant context, read the provider/model disclosure, and consent only for a synthetic brief. Begin with unknown jurisdiction and incomplete refund terms: expect follow-up questions, explicit tax/legal concerns and no importable draft. Fill the brief, confirm jurisdiction/economic terms, and provide only owned/verified resource identifiers when needed. A simple notification-only draft can demonstrate explicit review → editable canvas import → optional private save; it should never imply settlement. No publication, run or money execution is part of this drill.

Exercise the scenarios in `ux-study-scenarios.md`, especially account-switch delayed responses, revoked tenant provisioning authority, changed economic terms, and the disabled-provider/setup states. Record sanitized evidence in this track; preserve signing material and provider keys outside all research artifacts.
