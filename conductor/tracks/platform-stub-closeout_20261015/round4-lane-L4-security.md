---
type: lane-checkpoint
lane: L4-security
wave: W2
branch: r4/security
base: beae947
status: ready
---

# Round 4 · W2 · L4 security lead (`r4/security`, merge #6)

Helpers merged with `--no-ff`: A `r4/security-gates` @ `a5ebed24` (record `round4-lane-security-gates.md`),
B `r4/security-idor` @ `207d52bd` (record `round4-lane-security-idor.md`; one test conflict in
`ActionInvitationsTests.cs` resolved to the lead's durable-harness version, which supersedes B's keyed
`ActionManager` fix). Base `feat/platform-stub-closeout` (L1p, L2, L3 merged) merged in at `dea60a8c`
(one add/add conflict in `appsettings.IntegrationTest.json`: both keys kept — L1 `Webhooks`, L4 `Tenant`).
Closing vocabulary: **Built**; unit tests named below were run by filter; integration fixtures **Authored**.

## Scope closed (lead)

| Item | Where |
|---|---|
| Audit-2 H2 dapp-role | `Helpers/ClaimsPrincipalExtensions.cs` `CanAssignDappRoles`/`IsFirstPartyLogin`/`IsApiKey`; policy edited in place `Program.cs` `DappRoleAssignment`; `Controllers/UserController.cs:150`; manager refuses changing a current manager unless operator `Managers/UserManager.cs:276-281` |
| Audit-1 M1 claimed-user projection | `Managers/TenantManager.cs` `ToResponse(user, tenantId)` — claimed accounts expose ids only |
| LOW provisioning email squat | `Managers/TenantManager.cs:100` — tenant-supplied email never stored (synthetic `@onboard.azoa.local`) |
| M2 + External-wallet ownership proof | POCO `Persistence/SurrealDb/Models/WalletOwnershipProof.cs` (new table `wallet_ownership_proof`, id = sha256(chain:address)); store `Providers/Stores/Surreal/SurrealWalletOwnershipProofStore.cs`; `WalletAuthManager.VerifyAddressControlAsync`; `WalletManager.{CreateOwnershipChallengeAsync,ProveOwnershipAsync,ConnectWalletAsync}`; routes `POST /api/wallet/{id}/ownership-challenge`, `/ownership-proof` (`Controllers/WalletController.cs`); create/connect blocked only by another user's *proof* |
| WTT recipient proof seam (L5 ask) | `IWalletOwnershipProofStore.IsSignatureProvenAsync(walletId, ownerUserId)` — see Needs other lane L5 for the adapter swap |
| Audit-2 M8 legacy bridge | `Controllers/BridgeController.cs` `SigningRefusal` (transfer:sign, no act_as_tenant) on initiate/redeem/reverse; `Services/CrossChainBridgeService.cs` `OwnsReversalRecipientAsync` (Platform wallet or proven External) |
| LOW `HasSigningScope` | child tokens / `act_as_tenant` principals must carry the scope (`ClaimsPrincipalExtensions.cs:129`) |
| LOW credential revoke issuing tenant | `Managers/CredentialManager.cs:25-28` fail closed for tenant-delegated callers (provenance records issuer user only) |
| LOW ApiKey rotate | `Controllers/ApiKeyController.cs` rotate: active, unexpired, explicitly scoped, self-issuable; revoke-first; `SurrealApiKeyStore.RevokeAsync` typed active-key CAS |
| LOW `NetworkController` URL leak | `Controllers/NetworkController.cs` `PublicEndpoint` (strip userinfo/query, withhold key-shaped paths) |
| LOW key-rotation step-up | `Controllers/KeyRotationController.cs:45` `RecentNodeOperatorSession` |
| LOW faucet cap | `Managers/WalletManager.cs` `Blockchain:Faucet:MaxAmount` (default = DefaultAmount) |
| LOW `Controllers/AGENTS.md:219` | hosted-session text now names `RecentNodeOperatorSession`; new sections §wallet ownership proof, §legacy bridge, §smaller hardening; dapp-role section rewritten |
| SQL-01 / SQL-02 | `SurrealUserStore.cs:230,250,259` `resultIndex = statements.Count`; `SurrealWalletStore.TenantBootstrap.cs:102,112`. Probed 3.1.4: `BEGIN;LET;IF;RETURN;COMMIT` → 5 slots (COMMIT keeps its slot). Fixtures (Authored): `SurrealWalletAuthenticationTests.Committed_claim_returns_the_claimed_user_whatever_the_revocation_count(0/2)`, `SurrealConsentLifecycleTests.Tenant_bootstrap_returns_the_committed_wallet_on_create_and_on_exact_replay` |
| SQL-10 | `SurrealActionNodeExecutionStore.cs:106,227,239` → `ToKind`/`FromKind` name mapping |
| SQL-12 | probed: `option<string>` rejects NULL; `SurrealSagaStore.cs` writes `$_x ?? NONE` (5 sites). Fixture (Authored) `SurrealSagaStoreTests.CompleteStep_WithNullOutput_Succeeds_And_LeavesOutputAbsent` |
| SQL-13 Bridge | `SurrealBridgeStore.cs` three dynamic `Of(sql)` → typed `UpdateOnly` builders; `BuildConditionalUpdateSql` deleted |
| W1 note | `SurrealConsentGrantStore` read-only + typed; `UpsertAsync`, `ListByTenantAndParticipationRefAsync`, `RevokeAllByGrantorAsync` (no callers) deleted from store + interface |
| Helper A asks | deploy routes capture the credential → `StartWithCredentialAsync` (`ActionGraphBundleController`, `DappCompositionController`, managers); inventories refined |
| Helper B asks | `SurrealActionStore` persists `Dependencies` (`action_dependency`), fixture `SurrealActionStoreDependencyTests` (Authored); AGENTS cross-refs |
| L5 WTT asks | `Services/Actions/ActionWttRoute.cs` (`IActionWttRoute`, `ActionWttNodeHandler`); `IActionNodeHandlerRegistry.TryGetFor(node)` used by `ActionNodeStepHandler`; Bridge/Back output shape = legacy ∪ `WttActionOutput`; `Wallet` index `wallet_chain_address (chain_type, address)` (non-unique; no old-row impact) |
| CI failures in owned area | Actions (29), Auth (4), Controllers/TenantController (2): moved onto `DurableActionExecutionHarness` (owner credential witness + signing factory), parity tests use valid Algorand address + `expires:` message, tenant tests carry `auth_time`; output schema `LifecycleId/LifecycleState` |
| Stale doc | `Services/Actions/AGENTS.md` governance bullet (vote of record + L1 verdict gate) |

## Hunks

```hunk path=Program.cs
            if (!AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User))
                return ctx.User.CanAssignDappRoles();
```

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWalletOwnershipProofStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWalletOwnershipProofStore>();
```

Helper A's child-credential hunk and `appsettings*.json` `Tenant:DelegatedChildCredentials` hunks are in
`round4-lane-security-gates.md`.

## POCO deltas

- New table `wallet_ownership_proof` (generated `.surql` committed via the generator).
- `Wallet`: new non-unique index `wallet_chain_address`.
- Owner decision: schema moves by generation reset (no forward migration written).

## Published contracts

- `POST /api/wallet/{id}/ownership-challenge` → `AZOAResult<WalletChallengeResponse>` (sign `message`).
- `POST /api/wallet/{id}/ownership-proof` `{ signature, message? }` → `AZOAResult<WalletOwnershipProof{userId,walletId,chainType,address,provenAt}>`; 404 for a foreign/non-External wallet.
- `POST /api/wallet/connect` with `signedMessage` (+ `originalMessage`) records the proof inline.
- Bridge initiate/redeem/reverse: 403 without `transfer:sign` (scoped keys) or under `act_as_tenant`.
- Sweep body and helper contracts: see helper records.

## Compile result

`dotnet build azoa.sln -c Debug` on the merged tree (`dea60a8c`): **0 errors, 15 warnings** (≤ 25).
`node scripts/round4/merge-check.mjs`: ok. Secret scan `beae947..HEAD`: no hits.

## Tests run (filter, after the base merge)

Actions/Auth/Controllers/Consent/Wallet/Bridge/Credential/ApiKey/KeyRotation/Saga/Deployment/Holon/
DappComposition/ActionGraphBundle/Architecture/goldens: **1734 pass / 9 fail**. Remaining failures are
outside L4: L2/L3 inventory stale/duplicate rows and L1/L3/L5 unclassified rows (gate tests), operator BFF
`localStorage` in `frontend/.../genesis/page.tsx` (L7), `FrozenSoulboundClawbackTests` (NFT fee group, L5),
`EthereumNativeOperationTests` (L5 fake RPC NRE), `TinymanProviderQuoteTests` (L5).
New/changed tests: wallet ownership-proof (proof-only-blocks, verified-only record), bridge reversal to a
foreign address refused before any burn, dapp-role manager-on-manager + key-scope theory (Authored, HTTP),
network redaction table, registry WTT dispatch, durable-step unsupported type. Cut: `Publish_FanOutAction_Fails`
(premise gone — publish no longer rejects Control fan-out since `3d08ace`, the durable DAG scheduler runs it),
`ComposedDag GateFail_RoutesToRefund` (exercised the retired legacy synchronous executor's sibling semantics;
the durable engine parks on the failed gate), the CredentialManager-era `UpsertAsync`-never mock assertion.

## Checklists

- **Transaction scope:** SQL-01/02 batches unchanged except the absolute RETURN index; ApiKey revoke is a
  single typed CAS; proof store write is a single-record upsert on a deterministic id (rule 3); bridge
  mutations remain single conditional statements (same predicates); action-store dependency fan-out keeps
  the pre-existing non-transactional shape (recorded in `Providers/Stores/Surreal/AGENTS.md` §action-dependencies).
- **Security (changed routes):** actor/tenant from claims everywhere; ownership-proof routes owner-scoped,
  rate-limited, FirstPartyLogin; signature verified only after an atomic challenge consume; bridge value routes
  scope + no delegation + owned recipient; dapp-role tightened for keys and manager-on-manager; anonymous
  network endpoint no longer echoes credentials.
- **§7 classes:** statements-after-RETURN/positional offsets fixed (SQL-01/02); explicit-NULL raw writes fixed
  (SQL-12); ordinal casts removed (SQL-10); non-literal `Of` in Bridge removed (SQL-13); no new raw SurrealQL
  except the existing combined child SELECT/DELETE in `SurrealActionStore` (extended by one table).
- **Pruning gate:** unused consent-grant methods deleted; no catch-alls added (consent-grant store dropped its
  catch-all wrappers); `inheritdoc` on new implementations; IDE0005 clean.

## Needs other lane

- **L5:** swap `NoWalletOwnershipProof` in `Program.cs` (WTT hunk) for an adapter over
  `IWalletOwnershipProofStore.IsSignatureProvenAsync`; make `WttActionResolver : IActionWttRoute`
  (`Applies(node) => ReadConfig(node.Config) is not null`) and register it as `IActionWttRoute`; pass
  `IEnumerable<IActionWttRoute>` into `ActionNodeHandlerRegistry` (DI resolves it automatically once registered).
  `Managers/BlockchainOperationManager.Native.cs:41` still calls `StartAsync` without a credential (helper A note).
  CI: `FrozenSoulboundClawbackTests`, `EthereumNativeOperationTests`, `TinymanProviderQuoteTests`; classify
  UserNFT/EthereumRecovery routes in `L5.json`.
- **L1:** add `public WttActionConfig? Wtt { get; set; }` to `BridgeNodeConfig`/`BackNodeConfig`
  (`Models/Actions/NodeConfigs.cs`); classify webhook-delivery/admin-bootstrap/ecosystem store methods and
  `NodeOperator.SelectTenantProvider` route; `Services/AGENTS.md:130-133` Wormhole stub bullet stale.
- **L2:** `CrossTenant/L2.json` stale rows (combined "A / B" method names) and duplicate
  `HostedIdentityCallback.Callback` in `RouteAuthorization/L2.json`.
- **L3:** `CrossTenant/L3.json` "IHostedSessionStore (fiat checkout)" rows don't name a store type;
  classify `Allocation.Allocate`, `IBlockchainOperationStore.GetByUserAsync`.
- **L6:** HTTP flow with a real wallet signature through `/ownership-challenge` → `/ownership-proof` → bridge
  reverse to the proven address; real-JWT child-credential flow (helper A note).
- **L7:** SDK methods for the two ownership routes; parked-runs page reads `{scanned, results}`; operator BFF
  genesis page uses `localStorage` (fails `NodeOperatorSecurityContractTests`).
