---
type: UX Fixture Procedure
title: Coordinator-only tenant designer fixture
---

# Tenant designer fixture

The release coordinator requested a real marked tenant for an authenticated UI study. This document prepared a narrow exception to the ordinary read-only study; preparation did not authorize execution without the coordinator's refreshed-image, guard and scenario proofs. No role/key mutation, browser run or model request occurred during preparation. The coordinator later authorized and executed the guarded fixture, verified its developer role and live owner provisioning authority through real HTTP policies, and refreshed the UI login before pass 03. That pass used two synthetic model requests and closed as a recorded failure; fixture success is not assistant or release approval.

Use only the existing `azoa-e2e` project and its randomly generated operator fixture. Never use an owner's root key, production identity, signing material, direct database insert, token forgery, role-policy bypass or ad hoc manager bootstrap. The legacy E2E operator remains admitted by the real `DappRoleAssignment`/`Operator`/recent-session policies; if those policies refuse, stop and record the refusal instead of substituting stronger credentials.

The exact public API path is:

1. Standard session adapter creates the ordinary saved `qa-uxqa-ai-tenant` account through `POST /api/user/register` and real UI login. Its private generated email/password remain in `.secrets/accounts.json`.
2. Coordinator tool logs that account in through `POST /api/user/login`, reads `sub` only to address the API and verifies `GET /api/user/{sub}` returns the matching saved username/email and active top-level account. Decoding a claim alone is never identity proof.
3. Fresh `POST /api/operator/session` authenticates the existing disposable operator. Only if the target is still `dapp:user`, `PUT /api/user/{id}/dapp-role` assigns exactly `dapp:developer`. Existing developer is reused; manager/unexpected role is refused. Normal UI login is renewed after the assignment.
4. `GET /api/apikey` with the ordinary login looks only for the exact marker `uxqa-p02-designer-tenant`. Reuse its single active unexpired `tenant:provision` credential. Otherwise `POST /api/apikey/tenant` uses `{tenantUserId, name:"uxqa-p02-designer-tenant", expiresInDays:1}`. This existing route supplies fixed scopes; no scope escalation or custom key is invented. The returned raw key is discarded immediately; the designer's authority check needs only its durable active existence.
5. `GET /api/action-designer` with a fresh ordinary login must return that same owned tenant context. This read does not call OpenRouter. The tool reports whether the provider is available without exposing configuration secrets. Provider enablement remains the coordinator's separate process-local setup step.

Source seams in the combined checkout: `frontend/e2e/support/api.ts` already implements ordinary registration, operator sign-in and tenant-key issuance; `Controllers/UserController.cs` owns role assignment; `Controllers/ApiKeyController.cs` owns fixed-scope issuance/list/revoke; `Controllers/ActionDesignerController.cs` rechecks live owned tenant authority. No API authority is simulated by this fixture.

After independent review and explicit coordinator scenario authorization, root-level PowerShell commands are:

```powershell
# Existing UXQA_PLUGIN_LIB / UXQA_CONTAINER_RUNTIME / UXQA_E2E_ROOT still apply.
node uxqa/adapter/adapter.mjs guard
node uxqa/adapter/adapter.mjs seed uxqa/fixtures/tenant-designer-persona.json uxqa/.secrets/ai-sessions.json
$env:UXQA_COORDINATOR_FIXTURE = 'tenant-designer-p02'
try {
    node uxqa/adapter/prepare-tenant-designer.mjs
} finally {
    Remove-Item Env:UXQA_COORDINATOR_FIXTURE -ErrorAction SilentlyContinue
}
# Refresh browser login claims after the developer-role assignment.
node uxqa/adapter/adapter.mjs seed uxqa/fixtures/tenant-designer-persona.json uxqa/.secrets/ai-sessions.json
```

Count the new session file; never print it. The later UI scenario sets `UXQA_SESSIONS_FILE=uxqa/.secrets/ai-sessions.json`, verifies both guards again, starts a new trace and declares its bounded synthetic model-call budget before any user consent. The three model/service diagnostic requests recorded in the AI track do not authorize additional UI provider calls. No private draft save, publish, run or value action is implied by fixture preparation.

Cleanup is a separate coordinator step: ordinary-login `POST /api/apikey/{receiptKeyId}/revoke` can remove the marked fixture's provisioning authority. Use only the receipt-bound marked key; do not revoke other keys. A subsequent assistant request should then be refused before egress. Do not execute cleanup or role changes inside a UX lane.
