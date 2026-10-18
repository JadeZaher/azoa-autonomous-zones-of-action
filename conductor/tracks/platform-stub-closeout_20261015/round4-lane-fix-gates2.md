---
type: lane-checkpoint
round: 4
wave: final-fix
lane: fix-gates2
branch: r4/fix-gates2
base: feat/platform-stub-closeout@75b23986
inputs: .omc/research/round4-review/RV-X.md (R-2, R-3, R-4); RV-L4.md (RV-L4-09); RV-L5.md (RV-L5-R1, RV-L5-R2)
not-in-scope: store files owned by r4/fix-core2, Services/Fiat/**, Services/Reserve/**, on/off-ramp ledgers, Program.cs (no hunks)
---

# Round 4 · final fix · `r4/fix-gates2`

Stopped early on the coordinator's QA-freeze signal. Items 1–4 are DONE. Items 5–6 are NOT DONE and become post-QA
deferrals (see the end of this file).

## DONE

### 1. RV-X R-2 (MEDIUM): the value gate covers admission and the first value send only
- **WTT** (`Services/Wormhole/WttLifecycleService*.cs`):
  - The blanket gate on every continue is gone.
  - The gate now runs at admission and at `AuthorizeCurrentAsync` → `StageValueRefusalAsync`. That is the one
    first-send chokepoint every continue, submit and participant path reaches.
  - Source stages are gated.
  - Never gated: `Cleanup:*` and Cleanup-phase residual returns.
  - Target, participant and native-output stages are exempt when the recipient is the actor's own wallet
    (`OwnRecipientAsync`). A third-party delivery stays gated.
  - A workflow principal is checked against the reversal pause only (`ValueRailCapabilityGate.AccountStandingAsync`,
    extracted as a public static from the capability gate). A direct caller is checked against the full risk-hold-wrapped
    KYC gate.
  - New optional constructor dependency: `IFiatReversalLedger? accountPause`. DI resolves it as-is, so no Program.cs
    hunk is needed.
- **DEX** (`Services/Dex/SwapOperationService*.cs`):
  - Same who-rule: a workflow is checked against the pause only, a direct caller against the full gate.
  - An effect stage that follows a confirmed, differently named effect stage (`EarlierEffectConfirmedAsync`, a bounded
    walk over `PreviousStageId`) is exempt. A retry generation of the first stage is still gated.
- Docs: `Services/Wormhole/AGENTS.md` §Activation and the value gate; `Services/Dex/AGENTS.md` §Value gate and pin scope.

### 2. RV-X R-3 (MEDIUM): custodial operation gate
- `Managers/BlockchainOperationManager.cs` `ExecuteAsync` refuses user-owned `Transfer`, `Exchange` and `Swap` rows through
  `IValueAccessService` (risk-hold-wrapped). The check runs **before the idempotency claim**, so a refusal never burns
  the claim.
- Exempt:
  - a transfer to one of the owner's own wallets;
  - platform-owned rows;
  - Mint, Burn and IssueUnits.
- A missing `IValueAccessService` refuses (fail-closed). It is a new optional constructor parameter `valueAccess`, and DI
  resolves the registered `ValueAccessService`.
- Finding nuance: on the integrated tree the reviewer's listed callers are already covered:
  - `NftManager.TransferAsync` and the UserNFT transfer run through `NftLifecycleService`, which uses `IValueAccessService`;
  - Holon Mint and Exchange are refused by M9 in `BuildAndExecuteAsync`;
  - the allocation transfer gates the caller.

  This gate is defence in depth for any future caller. It also gates the source user of an allocation transfer.
- Docs: `Managers/AGENTS.md` §Custodial value gate.

### 3. RV-X R-4 (LOW)
- `Helpers/ActionSigningScopes.cs` `Bridge()` uses `WttActionResolver.Selects`, so a malformed `wtt` body selects the WTT
  pair, exactly as the registry does, and the WTT route's "malformed config" refusal wins.
- A top-level JSON fault still yields null.

### 4. RV-L4-09 (MEDIUM): stale foreign proof lockout
- New `IWalletOwnershipProofStore.IsHeldByAnotherUserAsync(chain, address, userId)` reuses `HolderProofIsValidAsync`. It
  returns true only while the other holder's External wallet still holds the address.
- `WalletManager.ProvenByAnotherUserAsync` uses it. The refusal message no longer points at the retired inline proof.
- New CrossTenant inventory row (`L4.json`).
- Docs: `Controllers/AGENTS.md` §Wallet ownership proof.

## Tests (flow-tests rules)
- **New:**
  - `tests/AZOA.WebAPI.Tests/Managers/CustodialValueGateFlowTests.cs`: real gate composition and real manager; only the
    chain is faked. The KYC-lapse / pause table covers:
    - third-party refusal before any claim, then a retry after reopening;
    - an own-wallet return runs while the gate is closed;
    - a paused swap is refused.
  - `WttLifecycleFlowTests.Workflow.cs`:
    - the own-wallet redeem completes after the source leg under a pause or KYC lapse;
    - a third-party redeem stays gated;
    - a workflow source send is stopped by the pause but not by a KYC lapse.
  - `SwapOperationServiceTests`: first-stage refusal vs a later effect stage exempt (table).
  - `ActionSigningScopeTests`: malformed-`wtt` rows.
  - Integration (authored, **not run**, needs SurrealDB): `SurrealWalletOwnershipProofStoreTests.Only_a_still_valid_foreign_proof_blocks_adding_the_address`.
- **Changed:**
  - `SwitchableValueGate`: adds a `KycLapsed` switch and exposes `Ledger`.
  - The WTT rig passes the pause ledger.
  - The 8 hand-built `BlockchainOperationManager` test constructions pass an approved `valueAccess`.
  - The `WalletManagerTests` proof theory mocks the new store method.
- **Run:** a filtered run of 296 tests (scope, wallet, proof flow, WTT, swap, custodial gate, BlockchainOperationManager*,
  client-signed, architecture/gate inventories): 0 failed.
- **Build:** `azoa.sln` has 0 errors and 16 warnings, none in touched files.

## Self-review
- **Transaction scope.** No new transaction spans an external call. Every gate is a read before a CAS, a claim or a send.
  Gate TOCTOU, where a flag lands between the check and the CAS, is accepted by design, as at the other seams.
- **Security.**
  - The WTT gate is at the only first-send chokepoint.
  - The DEX exemption requires a *confirmed* earlier effect of a different stage name on the same operation id.
  - The R-3 exemption requires the recipient to be the owner's own wallet on the same chain (`ChainAddressIdentity.Equivalent`).
  - No route or policy changes.

## NOT DONE: post-QA deferrals
- **5. RV-L5-R1 (MEDIUM, liveness, no value risk): owner cancel of an unsigned Ethereum client-signed plan.**
  - Design for the follow-up:
    - Add `ClientSignedOperationService.CancelUnsignedAsync(owner)` with a new owner route.
    - Cancel from `AwaitingSignature`, or from `NeedsOperator` without saved signed bytes, by setting Failed with
      outcome `Cancelled`.
    - Then release the `ethereum_preparation` `client-signed:{operationId}` reservation through a new
      `IClientSignedTransferModule` release method, so that `OwnsClientReservationAsync` can never pass again. That makes
      a later broadcast impossible.
  - **Trap found:** `IClientSignedOperationStore.TryTransitionAsync` is a status-only CAS. A `NeedsOperator` row can go
    NeedsOperator (unsigned) → PendingConfirmation (bytes saved) → NeedsOperator (parked with bytes). A cancel that read
    the first state would then win its CAS (ABA). The cancel needs its own store CAS that also requires
    `parameters.ClientSignedTransaction` to be absent, or a row version.
  - Also needed: the plan view and stuck list name the blocking (lowest open) nonce; SDK and frontend parity.
- **6. RV-L5-R2 (LOW):** not started.
  - Confirm the node-execution state while it waits on its external op (is it `Running`?).
  - Switch `WttSigningAuthorityFence.ActionStopped` to the `UPDATE … SET x = x` locking-read form.

## Needs other lane
None.

## Changed files
- `Helpers/ActionSigningScopes.cs`
- `Interfaces/Stores/IWalletOwnershipProofStore.cs`
- `Managers/BlockchainOperationManager.cs`
- `Managers/WalletManager.cs`
- `Managers/AGENTS.md`
- `Controllers/AGENTS.md`
- `Providers/Stores/Surreal/SurrealWalletOwnershipProofStore.cs`
- `Services/Actions/ValueRailCapabilityGate.cs`
- `Services/Dex/SwapOperationService.cs`, `.Commands.cs`, `.Exposure.cs`, `Services/Dex/AGENTS.md`
- `Services/Wormhole/WttLifecycleService.cs`, `.Stages.cs`, `.Followups.cs`, `Services/Wormhole/AGENTS.md`
- `tests/AZOA.WebAPI.Tests/Actions/ActionSigningScopeTests.cs`
- `tests/AZOA.WebAPI.Tests/Architecture/Inventories/CrossTenant/L4.json`
- `tests/AZOA.WebAPI.Tests/TestSupport/SwitchableValueGate.cs`
- `tests/AZOA.WebAPI.Tests/WalletManagerTests.cs`
- `tests/AZOA.WebAPI.Tests/Services/WttLifecycleFlowTests.Rig.cs`, `WttLifecycleFlowTests.Workflow.cs`
- `tests/AZOA.WebAPI.Tests/Managers/Dex/SwapOperationServiceTests.cs`
- `tests/AZOA.WebAPI.Tests/Managers/CustodialValueGateFlowTests.cs` (new)
- `tests/AZOA.WebAPI.Tests/BlockchainOperationManagerTests.cs`, `Managers/AmountIdempotencyKeyTests.cs`,
  `Managers/BlockchainOperationManagerExtendedTests.cs`, `Managers/ChainStampedSigningContextTests.cs`,
  `Managers/ClientSignedSolanaFlowTests.cs`, `Managers/ClientSignedTransferFlowTests.cs`,
  `Managers/ValueStampAmountRefusalTests.cs`, `Managers/WideAmountAndValueStampTests.cs`
- `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealWalletOwnershipProofStoreTests.cs`
- `conductor/tracks/platform-stub-closeout_20261015/round4-lane-fix-gates2.md` (this file)
