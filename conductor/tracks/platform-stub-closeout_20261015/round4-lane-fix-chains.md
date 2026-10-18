---
type: lane-checkpoint
round: 4
wave: fix-batch
lane: fix-chains
branch: r4/fix-chains
base: feat/platform-stub-closeout@21da6183
inputs: .omc/research/round4-review/RV-L5.md (RV-L5-01..08, -10, -11, -13); RV-X.md (X-2 chains part, X-6, X-8)
not-in-scope: RV-L5-09, RV-L5-12 / F-M11 (r4/fix-fm11); no edits to SurrealNftLifecycleStore*, NftLifecycleService*, Services/Users/**
---

# Round 4 · fix batch · `r4/fix-chains`

Everything below is *Built* (compiles, unit/flow tests run by filter) unless marked *Authored*.

## Finding → fix → test

| Finding | Fix (file:line) | Test |
|---|---|---|
| **RV-L5-01** one Ethereum tx settles two ops | Per-signer nonce reservation through the existing `ethereum_preparation` allocator table and its UNIQUE `ethereum_preparation_live_nonce` index: `Providers/Blockchain/Ethereum/EthereumProvider.ClientSigning.cs:56` (`ReserveClientNonceAsync`, nonce = max(pending, highest reservation + 1), `State=ClientReserved`, fingerprint = plan intent hash). One hash ⇒ one (signer, nonce) ⇒ one reservation ⇒ one row: `OwnsClientReservationAsync` `:90` is required before broadcast `:122` and before any `SamePayload` attribution, including the operator-supplied hash `:155`. Plans without a reservation id fail `ReadClientPlan`. Custody recovery refuses these rows (`EthereumProvider.Recovery.cs:80`) | `ClientSignedTransferFlowTests.Ethereum_identical_concurrent_transfers_from_one_wallet_get_their_own_nonces_and_one_transaction_never_settles_both` (nonces 7/8, distinct bytes, operator pasting tx A's hash onto row B refused, exactly one claim Completed) |
| **RV-L5-02** Solana false "never landed" | `Providers/Blockchain/Solana/SolanaProvider.ClientSigning.cs:82` (null status no longer concludes anything), `:99` `ProveClientPlanAbsenceAsync` reuses the WTT finalized-ancestry scan (`SolanaProvider.WttParticipantAbsence.cs:67`, now with an explicit start slot and per-pass budget), `:132` `FirstSlotPastValidityAsync` starts at the first finalized block past last-valid height. Cursor persisted between passes (`ClientSigningObservation.EvidenceJson`, `ClientSigningParameters.AbsenceEvidence`). A node without history → new verdict `Unproven` (`IClientSignedTransferModule.cs:60`) → parks `NeedsOperator`, never settles | `ClientSignedSolanaFlowTests.An_expired_plan_settles_only_from_finalized_history` ×3: history-less node → `NeedsOperator` + claim `InProgress`; full history, never landed → `Failed/Abandoned` + claim `Failed`; submitted, landed, node lost the status → `Completed` (the exact RV scenario (a)) |
| **RV-L5-03** claim not settled after a crash | `Services/Blockchain/ClientSigning/ClientSignedOperationService.cs:99` terminal rows re-settle from their own outcome (`RepairSettlementAsync` `:228`); reached by the receipt-triggered `ReconciliationService.ReconcileOperationAsync` (`includeTerminalForSettlement`) and by a replayed submit | `A_terminal_row_whose_claim_settlement_was_lost_repairs_on_the_next_reconcile` |
| **RV-L5-04** dropped/underpriced Ethereum rows invisible | `Providers/Stores/Surreal/SurrealOperationLogClientSigningStore.cs:40` lists `PendingConfirmation` rows past the TTL (over-fetch ×4, service keeps rows with a plan); rows keep re-observing; replacement/abandon only on chain proof (`Superseded` via an attributed, mined consumer) | `Ethereum_submitted_bytes_whose_nonce_another_transaction_finalized_close_as_superseded_only_once_attributed` (stuck list assertion) |
| **RV-L5-05** shared-nonce batching needs an operator | Confirmed: falls out of -01; concurrent plans from one signer get consecutive nonces and land in order | Same test as -01 (nonces 7 and 8) |
| **RV-L5-06** workflow WTT skips the own-wallet recipient rule | `Services/Wormhole/WttLifecycleService.cs:83` applies the rule to workflow principals too (no authorship exemption: the runner's value moves) | `WttLifecycleFlowTests.AGraphAuthorCannotRouteARunnersBridgedValueToAnAddressTheRunnerDoesNotOwn` (node `Failed`, zero quotes, nothing admitted) |
| **RV-L5-07** stranded workflow WTT op, no owner exit | Takeover allowed for action-bound ops (`WttLifecycleService.cs:300`) under the same `Ready/Target` rules once the run can no longer sign: `Providers/Stores/Surreal/WttSigningAuthorityFence.cs:37` `ActionStopped` (inverse of `ActionExecutionFence`) in the takeover transaction (`SurrealWttOperationStore.Exits.cs:31`). After takeover the binding is dropped for signing: store `Action()` `SurrealWttOperationStore.cs:241`, service `SigningActionOf` `WttLifecycleService.cs:407`, `AuthorityMismatch` `.Stages.cs:427` locks the workflow out | `TheOwnerTakesOverAWorkflowTransferStrandedAfterItsRunStoppedAndTheNodeIsLockedOut` (refused while live; after stop: owner settles, node witness `Forbidden`). SQL fence: *Authored* (integration sweep) |
| **RV-L5-08** operator consumer hash not proven mined | `EthereumProvider.ClientSigning.cs:160` + `FinalizedCanonicalReceiptAsync` `:181` (canonical block ≤ finalized) | `…superseded_only_once_attributed` (mempool-only consumer refused, mined one settles) |
| **RV-L5-10** RPC tokens in logs | `Providers/Blockchain/ChainRpcHttp.cs:16` `AddChainRpcClients` (`RemoveAllLoggers()` on every `chain-rpc:*` client), `LogSafeEndpoint`; init log `Core/Blockchain/Base/BaseBlockchainProvider.cs:164`; wired `Program.cs:1504` | `ModuleActivationRecoveryTests.A_logged_rpc_endpoint_keeps_only_scheme_host_and_port` |
| **RV-L5-11** D-7 pin flip strands in-flight swaps | `Services/Dex/DexModuleResolver.cs:53` `ResolveForExisting` (factory path, else `ChainModuleActivation.TryGetUngated` `ChainModuleActivation.cs:35`); every `SwapOperationService.*` command and `SwapRecoveryService` use it; quotes and the swap node admission keep the pinned `Resolve` | `ModuleActivationRecoveryTests.A_pin_admits_new_swaps_only_where_it_is_on_and_the_host_honours_it` |
| **RV-L5-13** M7 intent | Confirmed intended (decision below); no code change | n/a |
| **Railway env check** | Repo: `Dockerfile:68` sets `ASPNETCORE_ENVIRONMENT=Production`; `docker-entrypoint.sh:159` defaults to Production; `railway.json` sets nothing; compose files (dev/e2e only) set Development. A Railway service variable *could* override it (not verifiable without calling Railway). So pins now fail closed on any host carrying Railway's injected `RAILWAY_PROJECT_ID`/`RAILWAY_ENVIRONMENT_ID` unless `AZOA:Activation:DeployedHostAcknowledged=true` is set explicitly: `ChainModuleActivation.cs:30` `HostHonoursPins` (DEX/Exchange pins and `Wtt:Enabled`) | `A_pin_admits_new_swaps_only_where_it_is_on_and_the_host_honours_it` (Railway marker without ack → not admitted; with ack → admitted) |
| **X-2** (chains part) value gate on direct WTT, DEX, submit-signed | `IRealValueKycGate` (risk-hold-wrapped) at: `ClientSignedOperationService.cs:62`; WTT start `WttLifecycleService.cs:194`, continue `.Stages.cs:33`, native output `.Followups.cs:39` (helper `WttLifecycleService.cs:411`); DEX start `SwapOperationService.cs:36`, custody sign / first send `SwapOperationService.Commands.cs:33,50`, external disclosure `SwapOperationService.Exposure.cs:26` (helper `SwapOperationService.cs:121`; residual-return Cleanup stages stay open) | `A_paused_account_cannot_submit_a_signed_transfer_and_nothing_is_broadcast`; `AReversalPausedAccountCannotStartOrContinueATransferAndNothingIsSent`; `SwapOperationServiceTests.AReversalPausedAccountCannotAdmitASwap` (real `RiskHoldValueGate`, only the reversal ledger faked: `tests/…/TestSupport/SwitchableValueGate.cs`) |
| **X-6** WTT production switch | `Services/Wormhole/WttModuleResolver.cs:17` `AdmissionEnabled` (`Wtt:Enabled`, explicit true + host honours pins); checked at quote `WttLifecycleService.cs:63`, start `:193`, workflow admission `Services/Actions/WttActionResolver.cs:132`; admitted ops keep reconciling. Config: base `false`, Development/IntegrationTest `true` | `WithTheWttSwitchOffNothingIsQuotedOrAdmittedDirectlyOrByAWorkflow` |
| **X-8** malformed `wtt` falls back to the trusted bridge | `WttActionResolver.cs:40` `Selects` (any non-null `wtt` selects the route); legacy refusal `Services/Actions/Handlers/BridgeNodeHandler.cs:41`, `BackNodeHandler.cs:32`; publish-time refusal `Services/Actions/ActionNodeConfigRegistry.cs:160` | `BridgeBackNodeHandlerTests.A_node_naming_a_malformed_wtt_route_is_refused_at_publish_and_never_runs_on_the_trusted_bridge` ×2 |

## Decisions

- **RV-L5-13 / audit-2 M7 — intended.** The audit finding was that a binding survives an NFT transfer and lets the new
  holder raise permissions on the issuer's resource. Granting only when the current holder owns the target closes that.
  No product document (`conductor/product.md`, README) names token-gated access to another user's holon or wallet, and the
  only consumers are the two verify routes (`Controllers/UserNFTController.cs:242,253`). Cross-user token gating would be a
  new capability: an issuer-granted binding keyed on "created by the target's owner", with its own revocation story. Owner
  question, not a regression. Doc sentence requested below (I may not edit `Services/Users/**`).
- **Ethereum unsigned plans hold their nonce.** No release path: an Ethereum signature never expires, so a disclosed
  unsigned plan's nonce is never provably free. Later plans queue behind it until the wallet consumes that nonce. This is
  the honest ordering; recorded in `Providers/Blockchain/Ethereum/AGENTS.md` §Client-signed nonces.
- **`Unproven` parks rather than retries.** A node that cannot serve finalized history parks the row `NeedsOperator`;
  the operator re-resolve re-runs the scan (with a history node configured). Transient RPC failures before the scan stay
  `Flagged` (row unchanged) as before.
- **RV-L5-06 has no authorship exemption.** "Unless the actor authored the quest" would need the run to carry authorship
  into the WTT principal; a runner who is also the author can still pick any of their own wallets.

## Self-review

Transaction scope: the only new multi-table write is the takeover (`ActionStopped` reads + version CAS in one
`BEGIN…COMMIT`, `// raw:` comment, store `AGENTS.md` §WTT owner takeover). The nonce reservation is a single typed create
fenced by the existing unique index. The evidence write is a status-preserving CAS (a concurrent transition still wins).
Security: every changed route keeps its owner/tenant pinning; gates are added before any persistence or chain call; no
new route. §7 classes: absence proofs (RV-L5-02) only from complete finalized ancestry; idempotency always settled
(RV-L5-03); attribution only from mined canonical evidence (RV-L5-08). Tests: real providers / services over in-process
RPC, only process edges faked (RPC node, reversal ledger, wallet store); no mock-call assertions added.

## Verification

- `dotnet build azoa.sln -c Debug --no-incremental`: 0 errors, 17 warnings (baseline ≤ 25; none in touched files).
- Targeted tests: `ClientSigned|WttLifecycleFlowTests|BridgeBackNodeHandlerTests|ModuleActivationRecoveryTests|Swap|Dex|BlockchainOperation|Reconciliation|ActionNodeConfigRegistry` → 711 passed, 0 failed.
- Wider by filter (`Architecture|Reconciliation|Ethereum|Solana|Wtt|Dex|…`): 1294 passed, 4 failed — the four
  `RouteAuthorizationGateTests` / `CrossTenantGateTests` failures are RV-X X-3/X-4 (other lanes' inventory rows); none
  name a chains/WTT/DEX/client-signing row.
- `node scripts/round4/merge-check.mjs`: ok.

## Needs other lane

- **Services/Users/AGENTS.md §Bindings across a transfer** (r4/fix-fm11 or L-N owner): append "Cross-user token-gated
  access is intentionally not offered; an issuer-granted binding would be a new capability keyed on bindings created by the
  target's owner (owner decision pending, RV-L5-13)."
- **Services/Reserve/ExchangeHoldSourceService.cs:57** (L3 / r4/fix-money): an already-admitted exchange source resolves
  `IExchangeTransactionModule` through the pinned `TryGetModule`; use `ChainModuleActivation.TryGetUngated` for existing
  sources so an `Exchange:Activation` flip does not strand them (RV-L5-11, exchange half).
- **Managers/BlockchainOperationManager.cs:569** (`PlanClientSignatureAsync`, L1/L5 manager owner): planning an
  External-wallet transfer reserves an Ethereum nonce before the value gate runs at submit; if the coordinator wants plan
  creation itself gated, call `IRealValueKycGate` before `PlanClientSignedTransferAsync`.
- **Residual X-2** (r4/fix-identity per the coordinator): `IValueAccessService` consumers (hold, exchange, NFT, fungible,
  deployments) go through the wrapped gate there.

## Hunks

```hunk path=Program.cs
builder.Services.AddTransient<SolanaProvider>();
// RV-L5-10: named chain RPC clients without URI request logging (path-embedded provider tokens).
AZOA.WebAPI.Providers.Blockchain.ChainRpcHttp.AddChainRpcClients(builder.Services);
```

```hunk path=appsettings.json
  "__wttComment": "X-6: Wormhole token transfer admission switch (Services/Wormhole/AGENTS.md §Activation). Off by default; flips on only when that route's evidence gate (round-4 plan §8 gate 5) is green. Admitted transfers keep reconciling either way.",
  "Wtt": {
    "Enabled": false
  },
```

```hunk path=appsettings.Development.json
  "Wtt": {
    "Enabled": true
  },
```

```hunk path=appsettings.IntegrationTest.json
  "Wtt": {
    "Enabled": true
  },
```

## Inventory

Touched: `Providers/Blockchain/{ChainModuleActivation,ChainRpcHttp}.cs`, `Providers/Blockchain/ClientSigning/IClientSignedTransferModule.cs`,
`Providers/Blockchain/Algorand/AlgorandProvider.ClientSigning.cs`, `Providers/Blockchain/Ethereum/EthereumProvider.{ClientSigning,Recovery}.cs`,
`Providers/Blockchain/Solana/SolanaProvider.{ClientSigning,WttParticipantAbsence}.cs`, `Core/Blockchain/Base/BaseBlockchainProvider.cs`,
`Providers/Stores/Surreal/{SurrealOperationLogClientSigningStore,SurrealWttOperationStore,SurrealWttOperationStore.Exits,WttSigningAuthorityFence}.cs`,
`Services/Blockchain/ClientSigning/{ClientSignedOperationService,ClientSigningContracts,IClientSignedOperationStore}.cs`,
`Services/Wormhole/{WttLifecycleService,WttLifecycleService.Stages,WttLifecycleService.Followups,WttModuleResolver}.cs`,
`Services/Actions/{WttActionResolver,ActionNodeConfigRegistry}.cs`, `Services/Actions/Handlers/{BridgeNodeHandler,BackNodeHandler}.cs`,
`Services/Dex/{DexModuleResolver,SwapOperationService*,SwapRecoveryService}.cs`, `Program.cs` (1 line), `appsettings{,.Development,.IntegrationTest}.json`,
AGENTS.md: `Providers/Blockchain`, `Providers/Blockchain/Ethereum`, `Providers/Stores/Surreal`, `Services/Blockchain`, `Services/Wormhole`,
`Services/Actions`, `Services/Dex`. Tests: `Managers/ClientSignedTransferFlowTests.cs`, `Managers/ClientSignedSolanaFlowTests.cs` (new),
`Managers/Dex/{ModuleActivationRecoveryTests (new),SwapOperationServiceTests,SwapActionResolverTests}.cs`,
`Services/WttLifecycleFlowTests.{Workflow,Rig}.cs`, `Actions/Handlers/BridgeBackNodeHandlerTests.cs`,
`Controllers/BlockchainOperationControllerTests.cs`, `TestSupport/{SwitchableValueGate (new),InMemoryOperationLog,InMemoryWttOperationStore}.cs`.
No schema change (`ethereum_preparation.state` is a free string; `operation_log.parameters` is a JSON bag).
