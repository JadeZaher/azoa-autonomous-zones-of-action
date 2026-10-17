---
type: lane-checkpoint
lane: L4-helperA
branch: r4/security-gates
base: beae947
merges-into: r4/security (L4 lead merges with --no-ff; this branch never merges into feat/platform-stub-closeout)
---

# Round 4 W2: L4 helper A (gates, child credentials, deployments)

Scope: plan §4 L4 **Helper A**. Production code is **Built** (it compiles). The tests I wrote, plus the
deployment tests that already existed, were run by filter and pass. The two "every … is classified"
gate tests fail by design until every lane's inventory has merged. Owned files: `Helpers/ActionSigningScopes.cs`
(the orchestrator assigned it to helper A; plan §3.5 named helper B), `Controllers/DeploymentsController.cs`,
`Services/Deployments/**`, `Providers/Stores/Surreal/SurrealDeployment*.cs`, the child-credential pieces of
`TenantController.cs`, the `Program.cs` child-JWT block, the two gate tests and `Architecture/Inventories/**/L4.json`.
`DeploymentAuthorityFence` now lives at `Core/Surreal/DeploymentAuthorityFence.cs` (L1 moved it in W0). It
is unchanged here because the signing fence goes through `SigningAuthorityFence`.

## Scope closed

### Audit-1 H1: deployments sign only under the caller's captured credential and scope
- `Helpers/ActionSigningScopes.cs:47` `ForNativeIntent(kind, chain)` is the single "scope for this kind"
  (`Deploy`/`Recovery` → `contract:deploy:sign`, `Call` → `contract:call:sign`, `Account` on Solana →
  `solana:account:sign`). The action-node declaration and the deployment service both use it.
- `Services/Deployments/DeploymentService.Quote.cs:79` `ValidAuthority` now uses that helper. Before, `Account`
  on any chain mapped to the Solana scope.
- `DeploymentService.Quote.cs:87` `BindNativeAuthorityAsync`: legacy wallet + consent check, then
  `SigningAdmissionAuthorityFactory.BindDirectAsync(credential, wallet, chain, address, [ForNativeIntent(kind)])`
  for every signer. The credential must belong to the same actor and acting tenant. A direct row with no
  credential fails closed. An action row with no credential keeps its frozen workflow authority.
- `DeploymentService.Recovery.cs:165,205`: the bind runs before the Preparing CAS (after the budget-park
  checks, so a stage that cannot be afforded parks before custody) and before the SubmissionAuthorized
  CAS. The bound authorities pass into `IDeploymentStore.UpdateAsync(..., signing)`.
- `DeploymentService.LinkedRecovery.cs:79,170`: original-budget recovery and recovery start bind the same way.
- `Providers/Stores/Surreal/SurrealDeploymentStore.cs:114`: `Fenced` adds `SigningAuthorityFence.Create`
  (prefix `deployment_signing_{i}`) inside the CAS transaction. It also asserts that the credential's
  actor/tenant equal the row's. The signing failure marker is caught at `:99` and returns null; the service
  then reports "signing authority changed".
- `Controllers/DeploymentsController.cs`: the routes at the plan's `:24,63,86,97` (Start, Continue,
  RecoverWithinBudget, StartRecovery) capture the credential. Start calls the new
  `IDeploymentService.StartWithCredentialAsync`. The credential-less `StartAsync` overload is kept for
  existing callers and fails closed for native signing.

### ADV F-H12: deployment `NeedsOperator` has an exit
- `Services/Deployments/DeploymentService.Operator.cs`: `ListNeedsOperatorAsync` and `ResolveNeedsOperatorAsync`
  (`Reobserve` | `CloseUnresolvable`). The service always re-observes first, and a definitive chain verdict
  wins. Close goes through only while the observation is still unresolvable and the operator gives a
  bounded reason plus `AcknowledgeNoFurtherEffect`. The row then becomes terminal `Failed`/`OperatorClosed`,
  with the operator id and the unresolved wire in `EvidenceJson`.
- `Controllers/DeploymentOperatorController.cs` (new): `GET /api/operator/deployments` and
  `POST /api/operator/deployments/{id}/resolve` (`RecentNodeOperatorSession`, `financial` rate limit, no-store).
- `SurrealDeploymentStore.cs:147` `ListNeedsOperatorAsync` (typed LINQ). The worker scan (`ListPendingAsync`)
  is unchanged; a closed row drops out of it because `SubmissionAuthorized` becomes false.

### D-5: delegated child credentials (built; on only in test configs)
- `Core/DelegatedChildCredentialAttribute.cs` (new) declares `[DelegatedChildCredential(scope, signingScopes...)]`,
  the per-endpoint allowlist.
- `Services/Consent/DelegatedChildCredentialCatalog.cs` (new): the delegable scope vocabulary, which is the
  union of the allowlisted endpoints' declared scopes.
- `Services/Consent/DelegatedChildCredentialGate.cs` (new): per-request admission. It requires the node
  switch, an allowlisted endpoint, the endpoint scope on the token, an active issuing tenant, and a live
  grant covering the endpoint scope. This gives **issuing-tenant revoke**: tenant offboarding or a user
  revoking the grant kills every outstanding child JWT on its next request.
- `Program.cs:212-224`, the child-JWT block edited in place: the unconditional `context.Fail` is replaced by the gate.
- `Controllers/TenantController.cs:192-215`: issuance is re-enabled behind the switch (503 when off).
  Scopes are tenant key ∩ live grant ∩ request ∩ catalog, and child or acting-tenant principals cannot
  issue. The response is no-store and the route is rate-limited `tenant-custodial`.
- **Acting-tenant reads.** The allowlisted deployment endpoints are Generate, Artifact, Quote, Start, Continue,
  Cancel, List, Get and GetRequest. All of them read through `User.GetActingTenantId()`, and their rows
  filter by actor + tenant. Start and Continue also declare the three native signing scopes, so a child
  can satisfy `BindDirectAsync`, which still requires a live `UserExplicit` grant per signing scope.
- Config: `appsettings.json` `Tenant:DelegatedChildCredentials:Enabled=false`; new
  `appsettings.IntegrationTest.json` sets it to `true`. Production stays off until the sweep (D-5).

### Gates
- `tests/AZOA.WebAPI.Tests/Architecture/GateInventory.cs` (loader + reflection),
  `RouteAuthorizationGateTests.cs` (every mutating route classified exactly once; rows well-formed, not
  stale, routes match; child allowlist == inventory) and `CrossTenantGateTests.cs` (every owned-resource
  store method classified; rows well-formed, not stale). The schema is documented in
  `tests/AZOA.WebAPI.Tests/Architecture/AGENTS.md` §Gate inventories.
- `Inventories/RouteAuthorization/L4.json`: 126 rows covering every mutating L4 route, plus the allowlisted
  deployment GETs. `Inventories/CrossTenant/L4.json`: 103 rows covering every owned-resource method on the
  L4 stores. Deployment rows are specific. Rows for the lead's and helper B's stores and routes use
  heuristic text, which they should refine (see Needs other lane).

## Hunks

```hunk path=Program.cs
            if (AZOA.WebAPI.Core.AzoaClaims.IsChildCredential(principal))
            {
                // D-5: allowlisted endpoint + scope + live consent + active issuing tenant; see Services/Consent/AGENTS.md.
                var childGate = ActivatorUtilities.CreateInstance<AZOA.WebAPI.Services.Consent.DelegatedChildCredentialGate>(
                    context.HttpContext.RequestServices);
                var childRejection = await childGate.RejectAsync(
                    principal!, context.HttpContext.GetEndpoint(), context.HttpContext.RequestAborted);
                if (childRejection is not null)
                {
                    context.Fail(childRejection);
                    return;
                }
            }
```

```hunk path=appsettings.json
  "Tenant": { "DelegatedChildCredentials": { "Enabled": false } },
```

```hunk path=appsettings.IntegrationTest.json
  "Tenant": { "DelegatedChildCredentials": { "Enabled": true } }
```

No DI registration was added. The gate is built per request with `ActivatorUtilities`, and
`DeploymentService` takes `SigningAdmissionAuthorityFactory` as an optional constructor parameter; the factory
is already registered at `Program.cs:1105`.

## POCO deltas

None. No schema regeneration is needed.

## Published contracts (§4.9 L4: deployment operator resolution)

| Route | Body / result |
|---|---|
| `GET /api/operator/deployments?limit=` | `AZOAResult<DeploymentReceipt[]>`, parked rows oldest first |
| `POST /api/operator/deployments/{id}/resolve` | `{ expectedVersion, action: "Reobserve"\|"CloseUnresolvable", reason?, acknowledgeNoFurtherEffect }` → `AZOAResult<DeploymentReceipt>` (409 on refusal) |
| `POST /api/tenant/users/{id}/credential` | re-enabled when the switch is on: `{ scopes[] }` → `ChildCredentialResponse` (503 when off) |

## Compile result

`dotnet build azoa.sln -c Debug --no-incremental`: **0 errors, 22 warnings** (baseline ≤ 25; none in touched files).

## Tests run (by filter; flow-tests rules)

- New, all passing: `DeploymentDirectSigningAuthorityTests` (5). This includes a table of rejected
  credentials (none / scoped key without the kind scope / child of another tenant), each of which never
  prepares or submits. It also covers: owner binds and fences both CASes; revoked inside the transaction
  refuses before custody; operator close on an unresolvable observation; and close deferring to a live
  chain answer. `DelegatedChildCredentialFlowTests` (5): a real JWT minted through `TenantController` +
  `TenantManager`, clamped to the allowlist and admitted on Start, plus a per-request rejection table
  (switch off / not allowlisted / tenant deactivated / grant revoked).
  `RouteAuthorizationGateTests.Route_rows…`, `.Child_credential…` and `CrossTenantGateTests.Store_rows…` pass.
- Updated: `FirstPartyCredentialBoundaryTests.ChildCredentialEndpoint_IsUnavailable_WhileTheNodeSwitchIsOff`.
- `FullyQualifiedName~Deployments|DappComposition|ActionGraphBundleManagerTests|TenantManagerTests|TokenUseClaim`:
  149 pass, 3 fail, all 3 pre-existing in CI e0deb11 and outside this scope (see below).
- Expected to fail until all lanes merge: `Every_mutating_route_is_classified…` and
  `Every_owned_resource_store_method_is_classified…`. They are the sweep's completeness gates.
- Cut or declined: no getter or attribute-presence tests, and no mock-echo tests. The Prepare/Submit `Verify`
  calls assert the chain edge (signing never happened). They are not interaction trivia.

## Self-review (2 iterations)

1. **Transaction scope** (`round3-transaction-scope-review.md`). The signing guards share BEGIN…COMMIT with the
   deployment CAS. Every guard is a single statement, so `MutationIndex = guards.Count + 1` still holds. No
   external call happens inside the transaction. The fence failure is caught, and the expected outcome is
   typed. Iteration 1 found that the bind ran before the budget-park check, which broke
   `ExhaustedRecoveryBudgetParksBeforePreparationOrCustody`. It was moved after the park.
2. **Security checklist** for each changed route:
   - *Authz and signing scope:* `BindDirectAsync` + fence; DappManage; operator policy.
   - *Acting-tenant scope:* credential tenant == claims tenant == row tenant.
   - *Input validation:* operator reason is bounded with no control characters; request size limit.
   - *Secrets/PII:* child token returned no-store; no key material logged.
   - *Idempotency/replay:* version-fenced CAS, and close is idempotent through ExpectedVersion.
   - *No bare ids into record links:* there are no new raw links.
3. **Fail-closed classes (§7).** No new raw SurrealQL. The new store read is typed LINQ with literal state
   compare. No enum casts, no `;` in `Of`, nothing after RETURN.
4. **Pruning gate.** The stale "child JWTs temporarily disabled" paragraph in `Controllers/AGENTS.md` was
   rewritten. The rationale lives in `Services/Consent/AGENTS.md` §Delegated child credentials,
   `Services/Deployments/AGENTS.md` §Signing authority / §Operator resolution, and
   `tests/.../Architecture/AGENTS.md` §Gate inventories. Code has one-line doc comments only. No unused usings
   (the build enforces IDE0005).

## Needs other lane

- **L4 lead:**
  - `Helpers/ClaimsPrincipalExtensions.cs` `HasSigningScope` returns true for any JWT, child JWTs included.
    Add `if (AzoaClaims.IsChildCredential(principal)) return principal.HasScope(scope);` first. The gate
    already confines child JWTs to allowlisted endpoints; this is defense in depth.
  - `Managers/ActionGraphBundleManager.cs:157` and the `DappCompositionManager` deploy path call
    `IDeploymentService.StartAsync` with no credential, so native deploys now fail closed. Capture the
    credential in `ActionGraphBundleController.Deploy` / `DappCompositionController.Deploy` and call
    `StartWithCredentialAsync`.
  - Refine the heuristic `tenantCheck` and `enforcement` text in `CrossTenant/L4.json` and
    `RouteAuthorization/L4.json` for the lead's and helper B's stores and routes. The Bridge rows name
    `transfer:sign` as owed by M8.
  - Pre-existing CI failures: `DappCompositionManagerTests.DeployAsync_RejectsWhenSeriesIsNotReady`, and
    `TenantControllerRetryableResultTests` (custodial routes).
- **L5:**
  - `Managers/BlockchainOperationManager.Native.cs:41` uses `StartAsync` without a credential, so native
    fails closed. Thread the credential and call `StartWithCredentialAsync`.
  - Pre-existing: `EthereumNativeOperationTests.Finalized_cancellation_…` fails with an NRE in the
    Ethereum fake RPC (`EthereumPreparedRecoveryTests.Rpc.SendAsync:252`).
- **L2/L3/L5/L1:** to accept child JWTs on a route, add `[DelegatedChildCredential(scope, signingScopes…)]`
  plus `childCredentialScope`/`childSigningScopes` on that route's inventory row. Every lane must ship
  `Inventories/{RouteAuthorization,CrossTenant}/<lane>.json` in the §3.5 schema, or the completeness gates
  stay red.
- **L6:** add an HTTP integration flow with a real child JWT. `TestAuthHandler` bypasses `JwtBearerEvents`,
  so it has to go through the JWT scheme. It should cover issue → allowlisted deployment read → revoke
  grant → 401. `appsettings.IntegrationTest.json` enables the switch.
- **L7:** operator console page for deployment resolution (contract above) and the SDK methods for it.
