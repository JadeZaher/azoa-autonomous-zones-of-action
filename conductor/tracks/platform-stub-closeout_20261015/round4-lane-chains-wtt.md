---
type: lane-checkpoint
round: 4
wave: W2
lane: L5
helper: wtt
branch: r4/chains-wtt
base: feat/platform-stub-closeout@beae947
merges-into: r4/chains (L5 lead merges; this helper never merges into the feature branch)
---

# Round 4 · L5 WTT helper (`r4/chains-wtt`)

Scope: plan §4 L5 **Helper (opus) — WTT**. Everything below is *Built* (compiled, unit/flow-covered) unless
marked *Authored* (written, not executed: real-SurrealDB integration fixtures run in the sweep).

## Scope closed

| Item | Where | Notes |
|---|---|---|
| Delete the legacy fabricator (F-M8) | `Services/WormholeAdapter.cs:60` | `DeriveSequenceFromTxHash` (`string.GetHashCode`) and its only consumer path are gone. `InitiateTransferAsync` now **refuses before any source lock** (refusing after the lock would strand value with no sequence); the now-dead `NormalizeEmitterAddress` helper went with it. `IWormholeAdapter` stays registered (§3.4 rule 3; merge check passes) |
| `wtt` action resolver for Bridge/Back behind WTT `Enabled` | `Services/Actions/WttActionResolver.cs`; workflow authority in `Services/Wormhole/WttLifecycleService.cs:42,165`, `.Stages.cs:22,426`; fence `Providers/Stores/Surreal/WttSigningAuthorityFence.cs:12-55` | Domain `wtt`; request key `action:{run}:{node}:wtt` fixes the operation id; quote/admit/continue under the frozen `WorkflowSigningWitness` (must select exactly `wtt:source:sign`+`wtt:target:sign`+`action:execute`). Operation stores `ActionRunId/ActionNodeId/ActionIntentHash`; the fence accepts only that node's `Workflow` authority (with `ActionExecutionFence`) and refuses direct authority; direct callers cannot use `action:` keys. Missing/disabled route module → node `Failed` before any quote. **Wiring of the Bridge/Back handlers is L4's file — see Needs other lane** |
| Stage-key readiness **degrade** + rewrap | `Services/Wormhole/WttStageKeyReadiness.cs:23,46,73,85`; `WttStageKeyProtector.cs` (`IsConfigured`); `WttStageKeyVault.cs` (degraded result); gate `WttLifecycleService.cs:363`; operator routes `Controllers/WttOperatorController.cs:20,28` | Unconfigured/invalid keyring no longer throws at singleton construction; worker publishes the verdict at startup + every 5 min; new quotes/admissions refused (`503`) only on routes whose module carries the `IWttStageKeyModule` marker (Solana, `SolanaProvider.WttPreparation.cs:10`). Rewrap = bounded CAS batch onto the active key; runbook in `Services/Wormhole/AGENTS.md` §Protected protocol stage keys |
| Publication-mismatch exit | `Services/Wormhole/WttRecoveryService.cs:49` (`ReattributePublicationAsync`, via `resume-proof`) | Legacy journals parked with a `Confirmed` Transfer and no publication re-observe read-only and resume only with an exactly attributed publication; a `NeedsOperator` stage already self-heals through `reconcile`. Drift stays refused (proof binding requires frozen nonce/consistency) |
| Tenant-started-after-revoke exit | `WttLifecycleService.cs:282`, store `SurrealWttOperationStore.Exits.cs:12`, route `Controllers/WttBridgeController.cs:98` | Owner takeover records `OwnerIntentJson` (tenant removed, payer contexts rebound to the owner) only in `Ready`/`Target` with no target stage ever sent/disclosed, under the owner's direct `wtt:target:sign` fence; clears the admitted target quote; tenant locked out; replay idempotent. `IntentFor` replays every stage under the intent its quote bound |
| Absence proof for exposed-but-unsigned participant messages | `Services/Wormhole/WttRecoveryService.cs:102`; Algorand `AlgorandProvider.WttParticipantAbsence.cs`; Solana `SolanaProvider.WttParticipantAbsence.cs`; store `SurrealWttOperationStore.Exits.cs:40` | `ExpiredNoEffect` (Algorand txid absent past `lastValid`; Solana full finalized-ancestry scan adapted from the reviewed Jupiter observer, resumable cursor) resolves the stage so Continue issues generation+1; `Landed` (participant broadcast their own signature) re-enters acceptance and becomes `SubmissionAuthorized`. **Needs its own source review (RV-L5)**, as round 3 required |
| Accept L4's External-wallet proof at the old `WttLifecycleService.cs:263` | `WttLifecycleService.cs:339` (`OwnRecipientAsync`), seam `Interfaces/Wtt/IWttRecipientDirectory.cs` (`IWalletOwnershipProof`) | L4's `round4-lane-security.md` did not exist at authoring time. Contract coded against: **`IWalletOwnershipProof.IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken)`** returning true only for a verified signature challenge on an `External` wallet. Registered default `NoWalletOwnershipProof` is fail-closed (custodial-only, the interim round-3 reading). Implements the owner's original "the actor's verified wallets" decision; relayer fee stays `"0"` |
| Lane A final LOWs | see rows | `EthereumProvider.WttPreparation.cs:196` → `ReleasedReservation(row.State)`; `RetireStageAsync` refuses `Preparing` without `reservationProbedFree` (`SurrealWttOperationStore.Stages.cs:234`, caller `.Stages.cs:369`); the `"Ethereum"` literals in store cancel and service seal → "chain has an `IWttNonceReservationModule`" (`durableNonce` param; `.Stages.cs:387`); DB-clock offer-expiry fixture (*Authored*, `SurrealWttOperationStoreTests.TheDatabaseClockGovernsOfferExpiryEvenWhenTheHostClockLags`); recipient address index → `IWttRecipientDirectory` + `SurrealWttRecipientDirectory` (quote refuses a participant-step route to another user's custodial wallet); Algorand opt-in: tenant/workflow/custodial-recipient opt-in routes refused at quote, so no route waits on an opt-in nobody can sign; store fence now refuses tenant `Participant`/`NativeOutput` consent (round-3 "fence looser than service" LOW) |
| CI-failing SDK-independent WTT backend tests (`round4-ci-main-e0deb11.md`) | 8 failing → 0 | **Production bug:** `WttAlgorandGroup.Seal` used `SetFee`, which clamps to the 1000 µAlgo minimum, so zero-fee stateless verifier / undelegated protocol members got fee 1000 (wrong txids, payer over-pays, fee-mutation guard blind) → exact `Transaction.Fee` (`WttAlgorandGroup.cs:28`). **Production fix:** Solana `Verify` turns a noncanonical guardian address into `InvalidDataException` (`WttSolanaCoreInstructions.cs:35`). **Stale test:** holding-wire impersonation now expects the `InvalidDataException` wrapper the round-3 LOW introduced. Test helper `Indexed` reads `Fee ?? 0` (MessagePack omits zero). The 7 `wtt_*` schema-golden failures were already fixed by W1 |

## Hunks

```hunk path=Program.cs
builder.Services.AddScoped<WttStageKeyReadiness>();
builder.Services.AddSingleton<WttStageKeyReadinessState>();
builder.Services.AddHostedService<WttStageKeyReadinessWorker>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Wtt.IWttRecipientDirectory, AZOA.WebAPI.Providers.Stores.Surreal.SurrealWttRecipientDirectory>();
```

```hunk path=Program.cs
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Wtt.IWalletOwnershipProof, NoWalletOwnershipProof>();
```

(Planned in-place replacement: whoever wires L4's proof flag swaps `NoWalletOwnershipProof` for the L4 adapter on
this line and updates this block in the same commit.)

```hunk path=Program.cs
builder.Services.AddScoped<WttLifecycleService>();
builder.Services.AddHostedService<WttRecoveryWorker>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Actions.WttActionResolver>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Actions.IActionExternalOperationResolver>(sp =>
    sp.GetRequiredService<AZOA.WebAPI.Services.Actions.WttActionResolver>());
```

No config keys changed (`Wtt:*` untouched; the keyring stays an operator secret outside source control).

## POCO deltas

`wtt_operation` (new this round, not on `main`, not baselined): `action_run_id`, `action_node_id`,
`action_intent_hash`, `owner_intent_json` — all `option<string>`. Golden regenerated with the project generator
(`Persistence/SurrealDb/Generated/Schemas/wtt_operation.surql`, +4 lines; flowcharts unchanged). No backfill: no
old rows exist and no CAS reads these as required.

## Published contracts

- **Routes (L7 SDK + operator console):**
  - `POST /api/bridge/wtt/operations/{id}/owner-takeover` body `{ expectedVersion }` → `WttOperationView` (202);
    direct owner credential only; 403 tenant/insufficient scope, 409 sent/disclosed target stage or not
    `Ready`/`Target`; a replay after success returns the same view.
  - `GET /api/operator/wtt/stage-keys/readiness` → `WttStageKeyReadinessReport { status, configured, activeKeyId,
    availableKeyVersions, unreadableEnvelopeId, rewrapPending, detail, checkedAt }`.
  - `POST /api/operator/wtt/stage-keys/rewrap` → `WttStageKeyRewrapReport { rewrapped, conflicted, rewrapPending,
    readiness }`.
  - Existing `POST /api/operator/wtt/operations/{id}/resume-proof` now also resumes a parked publication.
  - New WTT stage state reachable from the public view: an exposed participant stage may become `ExpiredNoEffect`
    without any client call; `quotes`/`operations` may now answer `503` while the stage keyring is degraded.
- **Workflow node config (L1/L4):** Bridge/Back gain an optional object `wtt` shaped as
  `WttActionConfig { source{chainType,network,walletId}, tokenAddress, nativeAsset, amount, target{…},
  recipientAddress, sourceBudget, targetBudget, returnOfOperationId? }` (Back requires `returnOfOperationId`,
  Bridge forbids it). Output on success: `WttActionOutput { operationId, sourceChain, targetChain, recipientAddress,
  recipientAmount, targetTransactionId, bodyDigest, returnOfOperationId }`.
- **L4 proof flag:** implement `IWalletOwnershipProof` from the persisted challenge flag and replace the
  registration above in place.

## Needs other lane

- **L4 (`Services/Actions/Handlers/{BridgeNodeHandler,BackNodeHandler}.cs`):** when the node config carries `wtt`
  (`WttActionResolver.ReadConfig(node.Config) is not null`), the handler must behave as a recoverable handler on the
  `wtt` domain: `CreateReference(context, intentHash) => WttActionResolver.Reference(context, intentHash)`,
  `ResolveSigningScopes(node) => WttActionResolver.SigningScopes`, `HandleAsync => resolver.ExecuteAsync(context, ct)`
  (pattern: `SwapNodeHandler.cs`). Because `IRecoverableActionNodeHandler` is type-level, the clean shape is a
  registry wrapper (like `ActionNftOperationHandler`) in `ActionNodeHandlerRegistry` chosen per node config, or making
  both handlers recoverable and returning the legacy behaviour when `wtt` is absent — L4's call. Until wired, the
  resolver is registered but no node reaches it (inert, fail-closed).
- **L4 (`Services/Actions/ActionNodeOutputSchema.cs` / output catalog):** Bridge/Back `wtt` output shape above.
- **L1 (`Models/Actions/NodeConfigs.cs`, unlisted):** add `public WttActionConfig? Wtt { get; set; }` to
  `BridgeNodeConfig` and `BackNodeConfig` (strict deserialization would otherwise reject the `wtt` key at publish).
- **L4 (`Persistence/SurrealDb/Models/Wallet.cs`):** non-unique `[Index("wallet_chain_address", Fields = new[] {
  "chain_type", "address" })]` so `SurrealWttRecipientDirectory` is not a table scan on the quote path.
- **L4:** `IWalletOwnershipProof` implementation (above).
- **L1 (`Services/AGENTS.md:130-133`, §bridge):** the "WormholeAdapter.DeriveSequenceFromTxHash is a dev stub" bullet
  is now stale — legacy initiation refuses before any lock; point to `Services/Wormhole/AGENTS.md`.
- **L5 lead (inventory `Architecture/Inventories/RouteAuthorization/L5.json`):** rows
  `{route:"POST /api/bridge/wtt/operations/{id}/owner-takeover", controller:"WttBridgeController", action:"OwnerTakeover",
  class:"value-moving", scope:"wtt:target:sign", enforcement:"direct owner credential + WttSigningAuthorityFence(Target) in the takeover CAS"}`,
  `{route:"POST /api/operator/wtt/stage-keys/rewrap", controller:"WttOperatorController", action:"RewrapStageKeys",
  class:"non-value", scope:"RecentNodeOperatorSession", enforcement:"operator policy; CAS rewrap only, no signing"}`,
  `{route:"GET /api/operator/wtt/stage-keys/readiness", …, class:"non-value"}`. CrossTenant rows:
  `{store:"SurrealWttOperationStore", method:"TakeOverAsync", tenantCheck:"operation.TenantId non-empty + owner direct fence; service InScope(actor)"}`,
  `{store:"SurrealWttOperationStore", method:"ResolveExposedAsync", tenantCheck:"recovery-only, no caller scope"}`,
  `{store:"SurrealWttRecipientDirectory", method:"IsAnotherUsersCustodialWalletAsync", tenantCheck:"boolean only, no row disclosure"}`.
- **L5 lead:** `SolanaProvider.WttParticipantAbsence.cs` reuses your `ReadJupiterHistoryBlockAsync`/`JupiterBlockHeader`
  (`SolanaProvider.DexHistory.cs`); keep their signatures or move them to a shared helper.
- **L7:** SDK methods for the three routes; operator console pages (readiness/rewrap, takeover button on tenant
  operations in `AwaitingTargetAuthorization`); the existing `client.wtt.test.ts` failures are L7's.

## Scope extension (declared)

WTT-only files outside the literal helper globs, touched because the contracts above need them and no other lane
edits WTT-named files: `Interfaces/Wtt/{IWttParticipantModule,IWttNativeOutputModule,IWttStageKeyProtector,
IWttRecipientDirectory}.cs`, `Interfaces/Stores/{IWttOperationStore,IWttStageKeyStore}.cs`,
`Models/Wtt/WttAuthorityModels.cs`, `Persistence/SurrealDb/Models/WttOperation.cs` (+ its golden; L5 POCO),
`Providers/Blockchain/{Algorand,Solana}/AGENTS.md` (WTT paragraphs inside existing WTT sections),
`Providers/Blockchain/Ethereum/EthereumProvider.WttPreparation.cs` (lane A LOW), WTT test files.

## Compile result

`dotnet build azoa.sln -c Debug`: **0 errors, 22 warnings** (baseline ≤ 25). `node scripts/round4/merge-check.mjs`:
ok. Secret scan (`feat/platform-stub-closeout HEAD`): no hits. SDK/frontend untouched.

## Tests

Run by filter (brief allows own/failing unit tests): `Wtt|Wormhole|AlgorandGroupReceipt|Architecture|Bridge|
AttributePocoByteEquivalence` → **781 passed, 0 failed**. No integration/E2E runs.

Flows added (`tests/AZOA.WebAPI.Tests/Services/`):
- `WttLifecycleFlowTests.Exits.cs`: exposed opt-in expires unsigned → `ExpiredNoEffect` → Continue issues gen 1;
  participant self-broadcast → `Landed` → settles with zero custody signatures; owner takeover after grant
  revocation settles and locks the tenant out; takeover refused once a target stage was sent; legacy parked
  publication resumes only when attributed (drift refused); drifted publication parks and self-heals; recipient/
  participant-step table (tenant proven External with opt-in → 400, proven → ok, unproven → 403, another user's
  custody → 400, self-signing recipient → ok); stage-key routes refused until the keyring checks Ready while
  non-stage-key routes admit.
- `WttLifecycleFlowTests.Workflow.cs`: a Bridge node settles through the journal under its frozen witness (direct
  credential forbidden from continuing it or reusing its request key); narrow witness or participant route fails
  before any quote.
- `WttStageKeyReadinessTests.cs`: rotation degrades while a retained version is missing, rewrap, retire old key, same
  seed; absent keyring degrades and refuses protected use.
- *Authored* real-SurrealDB (`tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealWttOperationStoreTests.cs`):
  DB clock governs offer expiry with a lagging host clock; owner takeover fence/once; exposed stage resolves only from
  expiry or its own landed signature; EVM cancel/retire cases updated to the explicit `durableNonce` /
  `reservationProbedFree` contracts.

Unhappy paths kept: authority loss (revoked grant, tenant after takeover, direct vs workflow), sent-stage takeover
refusal, publication drift, unproven/foreign recipients, degraded keyring — each reachable and money-relevant.
Cut/declined: no unit test for `SurrealWttRecipientDirectory` (a one-line typed query; covered by the sweep's real-DB
run of the quote path once L4 adds the index); no separate ExecuteAsync test (thin wrapper over the tested
`ObserveAsync`).

## Checklists (self-review, 2 iterations)

- **Transaction scope** (`round3-transaction-scope-review.md`): `TakeOverAsync` — guards + version/state/
  `owner_intent_json = NONE` CAS in one `BEGIN…COMMIT`; the "no target stage sent" pre-read is pinned by the
  operation version (`// lock:` comment). `ResolveExposedAsync` — operation + stage CAS in one batch; `ResumeState`
  pre-read pinned by the operation version; Pending is stage-only CAS. No new in-batch SELECT; no chain call inside
  a transaction; named captures only. `ActionExecutionFence` marker now classified as an expected conflict.
- **Security** per changed route/service: owner-takeover (authz direct owner, `wtt:target:sign` bound and re-proved
  in-batch, tenant scope excluded, canonical id + version, idempotent replay, no secrets); operator stage-key routes
  (operator policy inherited, audit log, no key material in responses — versions and envelope id only); workflow
  path (frozen witness bound to run/node/intent, ActionExecutionFence, no participant/cleanup authority, request-key
  namespace reserved); recipient directory returns a boolean only. Flag for RV-L5: workflow recipients are chosen by
  the graph author (as with Transfer nodes); under a tenant run credential the own-wallet rule still applies.
- **Fail-closed classes (§7):** typed writers only; literal-null guards on new optional columns are the intended
  "not yet set" semantics; enum `WalletTypeKind.Platform` binds as written; no bare ids, no `;` in `Of`, no ordinal
  casts, no statements after RETURN.
- **Pruning gate:** dead `NormalizeEmitterAddress` removed with the fabricator; no catch-all swallowing (the readiness
  worker catches only to stay alive, logs once per tick, rethrows cancellation); `inheritdoc` on new interface
  implementations; IDE0005 clean; rationale in `Services/Wormhole/AGENTS.md` (§Recipient rule, §Workflow authority,
  §Operation exits, stage-key degrade/runbook).

## Deferred / not done

- Bridge/Back handler + node-config wiring (other lanes' files; listed above). The resolver is complete and inert
  until wired.
- Six-route protocol fixtures (L6 helper).
- A custody-signed opt-in for the actor's *own* custodial non-payer recipient: such routes stay refused at quote
  (deliver to the target payer instead); revisit only if tenants need delivery to a second custodial wallet.
