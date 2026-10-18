---
type: lane-checkpoint
lane: fix-security
wave: fix-batch
branch: r4/fix-security
base: 21da6183
status: ready
---

# Round 4 · fix batch · `r4/fix-security`

Inputs: `.omc/research/round4-review/RV-L4.md` (verdict FAIL) and, by coordinator scope addition,
`.omc/research/round4-review/RV-X.md` X-1, X-3, X-4 and X-5. RV-L4-02 (NFT recipient resolution) belongs to
`r4/fix-fm11` and was not touched; nothing under `Services/Users/**` or the NFT lifecycle files changed.
Closing vocabulary: **Built**. Unit tests below were run by filter; the store integration test is **Authored**
(needs the dev SurrealDB).

## Scope closed

| Finding | Fix | Where |
|---|---|---|
| RV-L4-01 HIGH child-token DApp-manager escalation | The `DappDevelop`/`DappManage` policies now always require the live role; a scope alone never admits. For delegated principals (child credential or `act_as_tenant`), the role is read only from the `azoa-live-role` identity that the gate stamps after loading the user from the store. A `dapp_role` claim carried in the token is ignored. Issuance drops `dapp:manage`/`dapp:develop` unless the user's live role holds it. The gate also refuses a role-bound endpoint scope the live role no longer grants. | `Helpers/ClaimsPrincipalExtensions.cs:36-66` (`HasRoleBoundAccess`, `TrustedDappRole`); `Program.cs:597-601` (policies edited in place); `Services/Consent/DelegatedChildCredentialGate.cs:15-55` (`AdmitAsync`, role check, live-role identity); `Managers/TenantManager.cs:314`; `Core/AzoaDappRoles.cs` `GrantsScope` |
| RV-L4-03 MEDIUM proof binding / replay | Added a purpose-bound ownership challenge (`AZOA-WALLET-OWNERSHIP-v1`, `purpose:wallet-ownership-proof`, user id, wallet id, chain, address, nonce, expiry). Verification resolves the challenge named by the echoed message's nonce (else the latest live one) and requires the stored text to equal the ownership text for this exact user and wallet. Login and ownership challenges are mutually unusable. The inline `connect` proof is retired. Supersession rule: another user's proof is replaced only once it is stale, meaning its wallet is no longer that holder's External wallet for the same identity. The replacement is a CAS on the old (user, wallet) pair, and `WalletManager` logs it with both ids. | `Managers/WalletAuthManager.cs:52-70,590-610`; `Interfaces/Managers/IWalletAuthManager.cs`; `Managers/WalletManager.cs:540-552,590-600`; `Providers/Stores/Surreal/SurrealWalletOwnershipProofStore.cs:34-95`; `Interfaces/Stores/IWalletOwnershipProofStore.cs`; `Models/WalletOwnershipProof.cs` |
| RV-L4-04 MEDIUM gate tests prove presence only | Route rows are keyed by verb + normalized template + action. Each row's `policies` must equal the reflected class+method `[Authorize]`/`[AllowAnonymous]`, and the gate fails when `enforcement` names a policy the action lacks. Verb-less (`[Route]`-only) actions fail, and so do minimal-API `Map{Post,Put,Delete,Patch,Methods}` calls. The child-credential flow test now drives HTTP: the real issue route through the real API-key handler issues a child JWT, which then passes JWT validation, the `AdmitAsync` hook and the production `DappManage` predicate. | `tests/AZOA.WebAPI.Tests/Architecture/GateInventory.cs`; `RouteAuthorizationGateTests.cs`; `tests/AZOA.WebAPI.Tests/Consent/DelegatedChildCredentialFlowTests.cs` |
| RV-L4-05 LOW operator close on action-linked rows | `CloseUnresolvable` is refused for rows with `ActionRunId`. They stay `NeedsOperator`, and only `Reobserve` is allowed. The inventory row is reclassified value-moving, because Reobserve can resubmit the saved wire. | `Services/Deployments/DeploymentService.Operator.cs:29-31`; `Services/Deployments/AGENTS.md` §Operator resolution; `RouteAuthorization/L4.json` |
| RV-L4-06 LOW child tokens outlive a revoked key | The child JWT carries `issuing_key_id`. Each request requires that tenant key to be active, unrevoked, unexpired and still carrying the endpoint scope, and requires the child user to be active. The issue route requires an API-key principal with an `ApiKeyId`. | `Core/AzoaClaims.cs` (`IssuingKeyId`, `LiveRoleIdentity`); `Managers/TenantManager.cs:429`; `Controllers/TenantController.cs:204-212`; gate `:35-46` |
| RV-L4-07 LOW network URL redaction | Endpoints are published as scheme + host + port only. Any path, or a key-shaped host label, withholds the endpoint (null, and the frontend keeps its default). | `Controllers/NetworkController.cs:106-119` |
| X-1 HIGH WTT re-authorization scope mismatch | `ActionSigningScopes.Required(Bridge/Back)` is route-aware: `wtt:source:sign` + `wtt:target:sign` when the config parses as a `wtt` request, `transfer:sign` for a legacy trusted bridge, and null (fail closed) on malformed JSON. The engine handler, the admission store fence (`SurrealActionNodeExecutionStore:86`, which had the same mismatch and would have refused every WTT admission) and participant re-authorization now agree. | `Helpers/ActionSigningScopes.cs:20,36-43`; `Services/Actions/AGENTS.md` §wtt-route |
| X-3 HIGH cross-tenant rows mis-keyed | All concrete `Surreal*` rows are re-keyed to the interface that declares the method, composites are split one method per row, the stale `IHostedSessionStore (fiat checkout)` rows are merged into the L2 row, and 107 unclassified owned-store methods are classified after reading each caller. | `Inventories/CrossTenant/L1..L4.json` |
| X-4 HIGH unclassified mutating routes | Added 18 rows: Allocation, genesis ×6, treasury ×2, backfill, NodeOperator ×3, NodeOperatorSession ×2, ExternalAuth ×3. The new keying also surfaced three `api/operator` alias routes (FiatEventOperator.Replay, OnRampLotOperator.Refund/Resume), which now have their own rows. The duplicate `HostedIdentityCallback.Callback` rows are now two distinct template keys. | `Inventories/RouteAuthorization/L1,L2,L3.json` |
| X-5 MEDIUM store blind spot | The gate scans every public `*Store`/`*Ledger` interface in the API assembly, plus a concrete `Surreal*Store`/`*Ledger` that implements none of them. A row keyed on a concrete class that implements a scanned interface is stale. | `GateInventory.StoreContracts`; `CrossTenantGateTests.cs` |

Notes for the record: **SQL-01/02 were false positives.** In 3.1.4 both BEGIN and COMMIT keep their result
slots (RV-L4 probe), so the absolute index stays as it is. **Pre-existing failure:**
`AllocationManagerTests.AllocateAsync_TransferStampsOpaqueCorrelationAndCallerInitiators` also fails on
base `21da6183` (checked in a throwaway detached worktree, since reaped). The two Algorand
"Overlapping…" tests time out only under the parallel run and pass in isolation.

**Deviation to review (RV-L4-01 status code).** A child token whose user lacks the manager role is refused
by the gate inside `OnTokenValidated`, which answers **401**, not 403. The gate refuses there because the
allowlisted deployment reads (List/Get/GetRequest/Artifact) carry no `DappManage` policy, so a policy-only
check would still let a non-manager child read through them. The policy layer still denies (403) any
delegated principal without the stamped live role (`Dapp_manage_scope_on_a_delegated_token…` test). In
practice a non-manager never obtains a child token with `dapp:manage`, because issuance clamps it.

## Tests (run by filter, once at the end)

`Consent|Auth|Managers|Actions|Wallet|Deployments|Architecture|Network|Signing|Tenant|Dapp|Wtt|Policy|ApiKey`
→ 2996 passed, 3 failed (the three described above). New or changed tests:

- `DelegatedChildCredentialFlowTests` (HTTP, TestServer). A manager child is clamped and reaches Generate.
  A non-manager's grant cannot delegate `dapp:manage`, and the resulting token is refused. Six lapses
  (demotion, switch off, tenant deactivated, grant revoked, issuing key revoked, user deactivated) each
  refuse Generate and Start, with zero service calls. A non-allowlisted endpoint returns 401. The policy
  ignores a token-carried `dapp_role`.
- `WalletOwnershipProofFlowTests` (real `WalletAuthManager` + `WalletManager`, a signature fake that binds to
  the exact bytes). The key holder proves their own wallet. A relayed login signature and a relayed
  foreign ownership signature are refused, both with and without the echoed message. An ownership
  challenge cannot log in.
- `ActionSigningScopeTests.Bridge_and_back_scopes_follow_the_route_the_registry_selects` (both kinds ×
  both routes).
- `DeploymentDirectSigningAuthorityTests.Operator_cannot_close_an_action_linked_operation…`.
- `NetworkEndpointRedactionTests` table, extended for path keys, percent-encoding and host tokens.
- `RouteAuthorizationGateTests` (5) and `CrossTenantGateTests` (2) are green on this tree.
- Authored (integration): `SurrealWalletOwnershipProofStoreTests` covers stale-holder supersession and a
  still-valid holder that is refused until it deletes its wallet.

Cut: the mock-echo `WalletManagerTests.ProveOwnershipAsync_RecordsTheProofOnlyForAVerifiedSignature`
(superseded by the flow test) and a store-failure passthrough test (tautological). The old gate-direct
child test was replaced by the HTTP version.

## Hunks

`OnTokenValidated` child step, edited in place. It supersedes helper A's block recorded in
`round4-lane-security-gates.md`:

```hunk path=Program.cs
            // D-5: allowlisted endpoint + scope + live consent/role + active issuing tenant and key; see Services/Consent/AGENTS.md.
            if (!await AZOA.WebAPI.Services.Consent.DelegatedChildCredentialGate.AdmitAsync(context))
                return;
```

DApp policies, edited in place:

```hunk path=Program.cs
    o.AddPolicy("DappDevelop", p => p.RequireAssertion(ctx => ctx.User.HasDappDevelopAccess()));
    o.AddPolicy("DappManage", p => p.RequireAssertion(ctx => ctx.User.HasDappManageAccess()));
```

No config, POCO or schema change. `wallet_ownership_proof` is unchanged; the CAS uses existing fields.

## Inventory (files touched)

Production: `Program.cs`, `Core/AzoaClaims.cs`, `Core/AzoaDappRoles.cs`, `Helpers/ClaimsPrincipalExtensions.cs`,
`Helpers/ActionSigningScopes.cs`, `Services/Consent/DelegatedChildCredentialGate.cs`, `Managers/TenantManager.cs`,
`Interfaces/Managers/ITenantManager.cs`, `Controllers/TenantController.cs`, `Managers/WalletAuthManager.cs`,
`Interfaces/Managers/IWalletAuthManager.cs`, `Managers/WalletManager.cs`, `Interfaces/Managers/IWalletManager.cs`,
`Interfaces/Stores/IWalletOwnershipProofStore.cs`, `Providers/Stores/Surreal/SurrealWalletOwnershipProofStore.cs`,
`Models/WalletOwnershipProof.cs`, `Services/Deployments/DeploymentService.Operator.cs`, `Controllers/NetworkController.cs`.
Docs: `Helpers/AGENTS.md` §Role-bound dApp policies, `Services/Consent/AGENTS.md`, `Controllers/AGENTS.md`
§Wallet ownership proof + §Public network endpoints, `Services/Deployments/AGENTS.md`, `Services/Actions/AGENTS.md`
§wtt-route, `tests/AZOA.WebAPI.Tests/Architecture/AGENTS.md` §Gate inventories.
Tests: as listed above, plus `TenantManagerTests`/`TokenUseClaimTests` (new `issuingKeyId` argument),
`TestSupport/InMemoryDeploymentStore.cs` (`Edit` fixture helper), and all `Inventories/**`.

## Needs other lane

- **SDK/frontend (L7):** `POST /api/wallet/connect` now refuses `signedMessage`. Neither the SDK nor the
  frontend sends it today (grep clean). An ownership-proof client should echo the challenge `message` so the
  server resolves that exact nonce.
- **Every lane adding routes or store methods after this merge:** add the row (with real `policies`) in the
  same change. Rule in `tests/AZOA.WebAPI.Tests/Architecture/AGENTS.md` §Gate inventories.
- **RV-L4-08 (fix-fm11 scope):** custodial NFT recipient lookup is case-sensitive. Not touched here.

## Deferred

- RV-L4-02 (fix-fm11). X-2, X-6..X-11 (not in this fixer's scope).
- The pre-existing challenge-burn class (RV-L4 "anyone can burn a victim's pending challenge") is
  narrowed, not closed. A client that echoes its message is now addressed by nonce, so a newer
  attacker-made challenge no longer displaces it. A garbage signature over a named nonce still consumes it.
  Trigger to revisit: per-challenge requester binding.
