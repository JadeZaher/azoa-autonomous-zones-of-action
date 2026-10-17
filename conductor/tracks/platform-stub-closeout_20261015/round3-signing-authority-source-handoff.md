---
type: implementation
status: awaiting-independent-source-review
---

# Shared signing authority source slice

This slice is authored, not runtime verified. No build, tests, schema generation, lint or typecheck has run; the user requires the final integrated sweep after all fixes.

## Authored behavior

- Direct credentials retain their authenticated scope ceiling and exact user authentication watermark. API keys separately pin current key identity/scopes/expiry; current scope expansion cannot widen the original authenticated principal. Only full login JWTs have owner authority. Node operator JWTs remain scope bounded and pin credential/session revisions.
- Token time parsing follows actual token classes (`auth_time` for login/operator, `nbf` for child, bounded `exp`/optional `iat`). Login/child issuers include consistent `iat`; the narrow HTTP revocation check now recognizes fresh login authentication evidence.
- Ordinary and deployed workflow invocation capture a private immutable run credential witness. Initial admission checks it within the run/source/outbox transaction. Original request replay returns the original run rather than replacing its authority.
- Economic node admission pins the original credential ceiling plus exact per-scope explicit grants, including `action:execute`, under the scheduler epoch transaction. Split grants remain supported. Node witnesses are immutable through ordinary outcome updates. Scope mapping uses resolved frozen input and explicit native operation kinds.
- A frozen legacy node missing evidence parks for participant authorization. The original actor and exact tenant can submit expected node version/hash, request ID and review reason. Current credential/grants, active run, unchanged node and exact parked saga are fenced together with an immutable authorization receipt and wake. Original run evidence is not backfilled. Receipt replay cannot wake a later generation. Existing node witnesses cannot be replaced by this path.
- Deterministic pre-dispatch rejection is persisted with admission. Crash recovery projects that receipt without calling a handler; missing transaction hashes remain insufficient evidence.

## Source inventory

New core paths: `Helpers/ValidatedTokenTime.cs`, `Helpers/ActionSigningScopes.cs`, `Models/Signing/SigningAdmissionAuthority.cs`, `Core/Surreal/SigningAuthorityFence.cs`, `Services/Signing/SigningAdmissionAuthorityFactory.cs`, `Services/Actions/ActionSigningAuthorizationService.cs`, `Providers/Stores/Surreal/SurrealActionSigningAuthorizationStore.cs`, `Controllers/ActionSigningAuthorizationController.cs`, `Models/Actions/ActionSigningAuthorizationRequest.cs`, and POCO `Persistence/SurrealDb/Models/ActionSigningAuthorization.cs`.

Existing integration paths: private fields in ActionRun/ActionNodeExecution models and POCOs; stores for run/node/workflow admission; `ActionNodeStepHandler`; `ActionOperationIdentity`; `IActionNodeHandler`; ordinary action controller/manager; frozen runtime interface/manager; narrow deployed Invoke interface/service/controller; login issuer, child issuer and Program token-validation region. Directory documentation records transaction rationale.

## Authored verification

- `ValidatedTokenTimeTests`: post-revocation fresh login, child timestamp, malformed/duplicate/class-mismatched times.
- `SigningAdmissionAuthorityFactoryTests`: operator token cannot become unrestricted owner; API-key concurrent scope expansion cannot widen principal authorization.
- `SurrealWorkflowSigningAuthorityTests`: split exact grants, concurrent node admission, revoked original despite replacement, changed watermark, widened/missing original credential, no-effect rejection persistence, explicit legacy authorization and later node admission, replay cannot wake later step, cancellation/grant/version/actor/gate conflicts roll back.
- Existing `SurrealActionNodeExecutionStoreTests` and `SurrealActionAdmissionSafetyTests` now use real versioned frozen inputs for admission assertions.

## Remaining implementation dependencies

This is not a claim that every signing domain is wired. B1 fungible, DEX, native deployment and WTT must consume the shared authority at their own pre-custody and first-egress boundaries. Original prepared-wire witnesses must remain immutable when an explicit current participant later authorizes continuation. The hold domain's exact scope contract remains its already-planned Round3 B2 work. Scope mapping does not grant unsupported hold operations implicit authority. Participant client discovery/authorization and broader old workflow fixture migration remain to integrate with their domain handoffs. These are implementation work, not external deferrals.

Central DI requests: scoped `SigningAdmissionAuthorityFactory`, `SurrealActionSigningAuthorizationStore`, `ActionSigningAuthorizationService`. New schema output is generated only at the final integrated sweep.
