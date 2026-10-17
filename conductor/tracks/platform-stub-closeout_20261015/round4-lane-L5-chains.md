---
type: lane-checkpoint
round: 4
wave: W2
lane: L5
branch: r4/chains
merge: 7
status: ready
---

# Round 4 · L5 chains (`r4/chains`, merge #7)

Base `feat/platform-stub-closeout` @ `beae947`; base re-merged after L4 (#6, `d45006a2`). WTT helper `r4/chains-wtt`
@ `a73d7c72` merged (`d230741f`; its record `round4-lane-chains-wtt.md`). Closing vocabulary: everything below is
**Built** (compiled + unit/flow-covered by filtered runs) unless marked *Authored*. No integration/E2E run.

## Scope closed

| Item | Where | Notes |
|---|---|---|
| **D-3 client-signed transfers** (liveness CRITICAL) | `Providers/Blockchain/ClientSigning/IClientSignedTransferModule.cs`; `{Algorand,Ethereum,Solana}Provider.ClientSigning.cs`; `Services/Blockchain/ClientSigning/*`; `Providers/Stores/Surreal/SurrealOperationLogClientSigningStore.cs`; `Managers/BlockchainOperationManager.cs` (`ExternalSourceWalletAsync`, `PlanClientSignatureAsync`, `ApplyChainResult` fail-closed); `Controllers/BlockchainOperationController.cs`; `Services/Reconciliation/ReconciliationService.cs` (delegation) | External-wallet Transfers get a saved exact-byte plan; `submit-signed` verifies bytes per chain (Algorand re-encode under the submitted sig; Ethereum unsigned RLP + recovered signer; Solana message + fee-payer sig), persists before broadcast (status CAS), reconciles to `Completed`/`Failed(FailedOnChain|Abandoned|Superseded)`; derived idempotency key persisted and settled on every terminal state; stuck TTL `Reconciliation:ClientSignatureTtlSeconds`; Ethereum unsubmitted or unattributed-consumed → `NeedsOperator`; operator list/resolve re-proves from chain. Plan-less "client signing" from a provider now fails closed. Design: `Services/Blockchain/AGENTS.md` §client-signed operations |
| `NeedsOperator` operation status | `Persistence/SurrealDb/Models/OperationLog.cs` (+ regenerated golden); `Migrations/20261017_130000__add_operation_log_needs_operator_status.surql` | Baselined table: param overwrite migration kept even under the generation-reset decision because the baseline overlay still defines the old `[Inside]` list |
| M9 | `Managers/BlockchainOperationManager.cs` `BuildAndExecuteAsync` | Caller-built (holon route) Mint/Exchange refused before any claim/row/provider call; `Managers/AGENTS.md` §caller-built operations |
| Acting-tenant reads | `BlockchainOperationController.InCallerTenant` | Tenant-acting caller sees only operations that tenant drove (Get/GetByUser) |
| Native deploy credential (L4 ask) | `Managers/BlockchainOperationManager.Native.cs:41` → `StartWithCredentialAsync(..., request.AuthenticationWitness)` | No production caller reaches this path today; test updated |
| **SQL-05** | `Services/Users/NftLifecycleService.cs` `CommitAsync` | `user_id` and `issuer_user_id` written as `record<user>` links (issuer was a second bare-id site) |
| **SQL-11** | `Providers/Stores/Surreal/SurrealSwapOperationStore.cs` (ClaimAsync quote guard) | `_swap_operation` bound only in the admission branch |
| Lost revoke cases | `Services/Users/NftLifecycleService.cs` (holder-address refusal); `tests/.../Credentials/CredentialRevokeRefusalTests.cs` | unrecorded issuer, non-soulbound, missing holder address, issuer without chain wallet, lookup error fails closed — all refused before clawback/admission |
| F-H12 NFT renewal past a confirmed stage | `Helpers/NftActorContinuationIdentity.cs` (`IsOwnerTakeover`, `TakenOver`); `NftLifecycleService.{ActorContinuation,Discovery}.cs`; `SurrealNftLifecycleStore.cs`; `Users/NftOwnerTakeoverTests.cs` | Owner's direct credential takes over a tenant-acting or workflow-admitted lifecycle; tenant locked out; run no longer fenced. `Services/Users/AGENTS.md` §NFT owner takeover |
| Revoke issuing tenant (`NftLifecycleService.cs:217,227`) | covered by the takeover + existing issuer check | Issuer under a revoked tenant continues via owner takeover |
| audit-2 M7 | `Services/Users/UserNFTService.cs` | Binding grants nothing / cannot be edited unless the target is owned by the current holder; target owner may remove |
| Prune `RefundNodeHandler` | `Services/Actions/Handlers/RefundNodeHandler.cs` | Unreachable body removed; declaration kept (registry wraps it with `ActionNftOperationHandler`) |
| LOW 6 | `SurrealNftLifecycleStore.cs` `Fenced` | Already absolute (`resultIndex = statements.Count` before RETURN) — verified, no change |
| `OperationStatus` mapping (W0) | `Helpers/NftLifecycleOperationProjection.cs:32` | **Confirmed** `Completed` (generic terminal success); no per-verb switch |
| D-7 declarations + pins | `AlgorandProvider` (+`IDexTransactionModule`, `IExchangeTransactionModule`), `EthereumProvider`/`SolanaProvider` (+`IDexTransactionModule`); `Providers/Blockchain/ChainModuleActivation.cs`; `*Provider.ModuleActivation.cs`; `BlockchainProviderFactory.TryGetModule` delegates | Advertised only where `{Dex|Exchange}:Activation:{Chain}:{Network}` is true; Development devnet on, production off until fixtures pass (L3 exchange is gated by the same switch) |
| F-M13 | `Providers/Blockchain/Ethereum/EthereumRpcEnvelope.cs` `ReadOwnEnvelope` | Type-2/1 with access lists and EIP-155 legacy reconstructed and re-proven; pre-155 refused; `Managers/Dex/EthereumOwnEnvelopeTests.cs` |
| DexOperatorController:21 | `Controllers/DexOperatorController.cs` | Logs outcome after the call (refused vs settled) |
| CleanupReady self-hold | — | Not changed: owner-recoverable by design (`Services/Dex/AGENTS.md` §Cleanup semantics); left for RV-L5 |
| D6 | `EthereumRecoveryController`, `EthereumOperatorRecoveryController` (`RecoveryModule`), `EthereumProvider.Recovery.cs` | Unknown network and refused/unconfirmed resume send → 409 |
| D9 | `Providers/Blockchain/ChainRpcHttp.cs`; three providers; `Program.cs` Algorand registration | `IHttpClientFactory` named clients `chain-rpc:{chain}` |
| RPC URL scrub | `EthereumProvider.GetChainInfoAsync`, `SolanaProvider.GetChainInfoAsync` | `rpcEndpoint` removed |
| Prune `swap_state` | `Models/SwapState.cs`, `Generated/Schemas/swap_state.surql`, `CompatibilityBaselines/swap_state.surql` (+ README row) deleted; `swap_state` half removed from `Migrations/20260718_136000` | L1 ask taken at the merge signal (generation reset makes the migration checksum change safe); `SurrealDeploymentLeastPrivilegeContractTests` updated |
| `OperationStatus.NeedsOperator` | `Models/OperationStatus.cs` | L1 ask taken at the merge signal; `ClientSigningStatus.NeedsOperator` now aliases it |
| CI-failing tests in my globs | see Tests | Production fixes: zero-amount NFT fee authorization (`AlgorandProvider.Nft.cs`), exact-bytes Algorand signer (`AlgorandTransactionSigner.cs`), `AlgorandSignedEnvelope.MatchExact` (Tinyman), pooled zero fee/amount in `AlgorandGroupReceipt` (also L3 Hold ask), zero-scalar framing in `EthereumSignedEnvelope` |
| L3 ask: null-amount group receipt | `Providers/Blockchain/Algorand/AlgorandGroupReceipt.cs` | zero-amount axfer/pay members match an absent amount; SDK-decoded zero axfer (`AssetAcceptTransaction`) handled |
| L4 asks | `Services/Actions/WttActionResolver.cs` implements `IActionWttRoute` (registered); `WalletOwnershipProofAdapter` replaces `NoWalletOwnershipProof`; `Models/Actions/NodeConfigs.cs` `Wtt` on Bridge/Back (L1 file, taken per coordinator) | — |
| Inventories | `Architecture/Inventories/RouteAuthorization/L5.json`, `CrossTenant/L5.json` | All L5 + WTT routes/stores classified; gate tests show no L5 rows missing |

## Hunks

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Services.Blockchain.ClientSigning.IClientSignedOperationStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealOperationLogClientSigningStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Blockchain.ClientSigning.ClientSignedOperationService>();
```

```hunk path=Program.cs
    httpClientFactory: sp.GetRequiredService<IHttpClientFactory>()));
```

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Wtt.IWalletOwnershipProof, WalletOwnershipProofAdapter>();
```

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Services.Actions.IActionWttRoute>(sp =>
    sp.GetRequiredService<AZOA.WebAPI.Services.Actions.WttActionResolver>());
```

```hunk path=appsettings.Development.json
      "Algorand": { "Devnet": true },
      "Ethereum": { "Devnet": true },
      "Solana": { "Devnet": true }
```

(The helper's three Program.cs hunks are in `round4-lane-chains-wtt.md`; its `NoWalletOwnershipProof` line is replaced
in place by the adapter line above.)

## POCO deltas

- `operation_log.status` `[Inside]` + `NeedsOperator` (golden regenerated; forward param-overwrite migration).
- `SwapState` POCO removed (golden removed; `POCO count = schema file count` holds: 133).
- Helper: `wtt_operation` +4 `option<string>` fields (its record).

## Published contracts (§4.9)

- `GET /api/blockchain/operations/{id}/signing-plan` → `ClientSigningPlanView { operationId, status, chainType, network,
  protocolIdentity, format, signer, unsignedPayloadBase64, predictedTransactionId?, validThroughHeight?, nonce? }`.
- `POST /api/blockchain/operations/{id}/submit-signed` body `{ signedTransactionBase64 }` → `BlockchainOperationResponse`
  (200); 404 not owner/no plan, 409 already signed with other bytes / terminal, 422 `CLIENT_SIGNATURE_MISMATCH`, 503.
- `GET /api/operator/blockchain-operations/stuck?limit=` → `StuckClientSignedOperationView[]`.
- `POST /api/operator/blockchain-operations/{id}/resolve` body `{ reason, transactionId? }` → response (200) or 409 when
  the chain does not settle it.
- Holon `POST /api/holon/{id}/mint|exchange` now answer 400 `OPERATION_NOT_ALLOWED` (L7: point users at `/api/nft/mint`).

## Deferred (named, not silent)

- **F-M11 recipient-stage Solana costs**: needs a participant-accepted cost quote on the Accept/Transfer stage (protocol
  cost offer/acceptance like the mint path). Not built this lane (scope/time); fails nothing today — recipient spends rent
  under stage consent without a separate cost quote. Owner: fix batch / RV-L5.
- **LOW 5 fixtures**: real-DB renewal/participant-lapse fixtures belong to L6 harness; the in-memory store mirrors the
  fences used here.
- **Owner takeover of a still-running workflow lifecycle**: allowed (owner owns the asset); RV-L5 to confirm.
- `AlgorandProviderTransactTests.OverlappingPreparationWinsOneUnsignedPlanAndNeverSubmits` times out only under full
  parallel load (passes alone; CI showed the same timeout) — harness timing, not production.

## Needs other lane

- **Declared out-of-glob edits (L1 closed):** `Models/OperationStatus.cs` (`NeedsOperator`), `Models/Actions/NodeConfigs.cs` (Bridge/Back `Wtt`), `Persistence/SurrealDb/CompatibilityBaselines/**`, `Migrations/20260718_136000`, `tests/.../Core/SurrealDeploymentLeastPrivilegeContractTests.cs`.
- **L7**: SDK + UI for submit-signed (fetch plan → wallet signs exact bytes → submit), operator stuck list/resolve; holon mint/exchange removal; WTT helper routes.
- **L6**: real-DB fixture for `SurrealOperationLogClientSigningStore` CAS race (two submits, submit vs expiry reconcile); protocol fixtures decide the D-7 production pins.
- **Other lanes' gate rows**: CrossTenant/RouteAuthorization gate failures remaining after this merge are L1/L2/L3 stale or missing rows (none L5).

## Compile result

`dotnet build azoa.sln -c Debug` → **0 errors, 15 warnings** (≤ 25). `node scripts/round4/merge-check.mjs` → ok.
Secret scan (`feat/platform-stub-closeout HEAD`) → no hits.

## Tests (filtered, own area)

Signing, Managers.Dex, Users, Credentials, Wtt, Reconciliation, BlockchainOperation, Nft, Ethereum/Algorand/Solana,
Tinyman/Jupiter: **1539 passed, 1 load-flaky** (above). New flows: `Managers/ClientSignedTransferFlowTests` (Algorand
exact-signature broadcast once + confirm + claim settled; Algorand abandoned after window; tenant-driven never planned;
Ethereum unsigned park → operator settles only chain-proven outcome (superseded / signer self-broadcast); Ethereum
submitted + unattributed consumer parks, attributed → superseded), `Credentials/CredentialRevokeRefusalTests`,
`Users/NftOwnerTakeoverTests`, `Users/UserNftBindingAccessTests` (transfer case), `Managers/Dex/EthereumOwnEnvelopeTests`,
`BlockchainOperationManagerTests` (M9, plan-less client signing fails closed). Cut: the plan-less AwaitingSignature test
(premise retired), RefundNodeHandler HandleAsync tests (body pruned). Not mine and still red in CI: `ComposedDagDeterminismTests` (Actions, failing before this lane), Kyc/Workflow tests seen in filters.

## Checklists

- **Transaction scope**: new store `SurrealOperationLogClientSigningStore` — single-record status CAS (`WHERE status = expected`),
  retried only on engine conflict, loser re-reads; signed bytes persisted before the broadcast; no transaction spans a chain
  call. SQL-11 changes bindings only. NFT store: takeover gates the action fence; cancel tenant pin follows takeover.
- **Security**: submit-signed — owner + acting tenant pinned, `transfer:sign`, exact bytes, rate-limited; resolve —
  operator policy, chain-proven only, audited; plan view `no-store`. M9 removes platform-key mint from a caller route.
  No secrets/PII in results; RPC URL scrubbed. No bare ids into links (SQL-05 fixed).
- **§7 classes**: binding names (SQL-11 fixed), positional offsets (none added), bare ids (SQL-05), enum casing (status
  written via typed enum), no `;` in `Of`, no ordinal casts.
- **Pruning gate**: IDE0005 clean; one-line docs + directory AGENTS.md sections (Services/Blockchain, Providers/Blockchain,
  Algorand, Services/Users, Managers).
