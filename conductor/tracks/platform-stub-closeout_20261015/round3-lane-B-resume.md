---
type: lane-checkpoint
lane: B
domain: Hold / exchange settlement / reserve cutover
status: source-complete
resumed: 2026-10-16
closed: 2026-10-16
---

# Round 3 lane B resume checklist

Resumed in Claude Code from checkpoint `3d08ace` (Codex rollout `01a13fa6`, stopped 23:04Z mid-slice).
Rules: `round3-claude-resume.md`. Source only: nothing was built, tested, linted or committed, and the
generated `.surql` is untouched. Everything below is **source-complete and runtime-unrun**. The single
integrated sweep after round 4 is the first execution.

## Done before cutoff (source authored, independently reviewed where noted, runtime unrun)

- B2 Algorand value preparation prerequisite (`round3-hold-value-prerequisite-handoff.md`).
- Local reserve open/refund/chain command store with immutable receipts (`round3-hold-local-source-handoff.md`).
- Rail admission, quote consent, preparation/first-send, finalized accounting, recipient acceptance,
  no-effect evidence, replacement, cancellation, discovery, restricted reconciliation worker
  (`round3-hold-rail-source-checkpoint.md`).
- Direct and workflow-origin release mandates, disclosure recovery (`round3-hold-mandate-source-checkpoint.md`).
- Frozen quartet adapter + ordered request reconciliation, SDK `hold-commands` module, `/holds` recovery
  panel (`round3-hold-adapter-client-checkpoint.md`, Root-accepted adapter source).
- Exchange source journal with two-party authority, commitment, wire journal, finalized settlement,
  proven-absence compensation (`round3-exchange-source-checkpoint.md`).
- Algorand exchange wire module (not advertised) + fixtures (`round3-exchange-provider-wire.md`).
- Exchange automatic-release mandate composed into the opening transaction (`ExchangeMandate` partial),
  exact settled-source release fence (`HoldReleaseExchangeFence`), absence closes mandate.

## Round-3 items (all closed at source)

- [x] B3-01 Exchange authority persistence fixtures: revocation before preparation (tenant key,
      participant grant, wallet rebind), first-send boundary, settlement after authority loss,
      concurrent admission. Files: `SurrealHoldCommandTests.ExchangeAuthority.cs`, plus the existing
      `SurrealExchangeAuthorityTests.cs`.
- [x] B3-02 `AlgorandExchangeGroupSubmitter` and `ExchangeBundleDefinition` are deleted. Every exchange
      kind runs through the durable source and `IExchangeTransactionModule`. The module stays
      unadvertised, so exchanges fail closed until independent review.
- [x] B3-03 Settled-source exact release, plus same-key disclosure revision before commitment
      (`ReviseExchangeReleaseAsync`, versioned `ExchangeReleaseRevise` audit). Fixtures are in
      `SurrealHoldCommandTests.ExchangeMandate.cs`.
- [x] B3-04 `HoldLocalCommandPlan` refuses local Refund/ReleaseIntoHold of a liability with an economic
      source. A local refund or chain closes an Active mandate atomically, and completion cancels a
      displaced mandate. Fixtures: `.LocalSettlement.cs`.
- [x] B3-05 Held buy / `Issue` / `Return` commitment, settlement and absence compensation
      (`SurrealHoldCommandStore.ExchangeUnits.cs`, `ExchangeHoldWirePlan`). Fixtures: `.ExchangeUnits.cs`,
      `ExchangeHoldWirePlanTests.cs`.
- [x] B3-06 `ExchangeSourceReconciliationWorker` plus discovery: observe, rebroadcast, settle, and
      abandon stale sources that were never sent. Fixture: `Hold/ExchangeHoldSourceRecoveryTests.cs`.
- [x] B3-07 `ReserveExchangeManager` and `ExchangeController` cut over (captured credential, source
      receipts). `ExchangeSettlementReconciler` now only refuses retired rows. Manager flow fixtures:
      `.ExchangeFlow.cs`.
- [x] B3-08 Fiat reversal recovers exact partial purchases with a liability split and allocation
      lineage. Provenance now comes from settled sources. Fixtures: `.ExchangeRecovery.cs`,
      `Fiat/ReversedPurchaseProvenanceTests.cs`.
- [x] B3-09 Legacy `HoldController`, `PlatformHoldService`, `HoldSweepWorker`, `IPlatformHold`,
      `HoldOptions` and `SurrealHoldLedgerStore` retired. Their callers were migrated and their tests
      deleted.
- [x] B3-10 `HoldRegistration.AddPlatformHold` registers stores, services, the action resolver and both
      workers. The Hold quartet signs under `transfer:sign`.
- [x] B3-11 Legacy hold SDK methods removed. Exchange release disclosure types added. The `/holds`
      page has release-mandate management (`hold-mandate-panel.tsx` and its test). Direct admission
      UI is carried forward (below).
- [x] B3-12 Contract amendment proposed: `round3-value-custody-parity-amendment.md`. Implementation is
      round 4.
- [x] B3-13 Pruning, plus directory AGENTS.md rewrites: `Services/Hold`, `Services/Reserve`
      §durable-exchange, `Providers/Stores/Surreal`, `Services/Fiat/Reversal` §provenance and
      `Core/Surreal`.
- [x] B3-14 Stages do not stall. They are renewed through `/continue`, `/accept` (new RequestId) and
      `/reauthorize`. See `Services/Hold/AGENTS.md` §participant-renewal.
- [x] B3-15 The Hold quartet bodies are pruned to durable-admission descriptors. The NFT
      `RefundNodeHandler.HandleAsync` is unreachable in production, because `ActionNftOperationHandler`
      wraps `Refund`. Its only callers are lane-N tests, so pruning it is a request to lane N.

## Coordinator requests handled

- Fence prefix fix (`SigningAuthorityFence.Sql`) was not reverted. Checked: `ExchangeAuthorityFence`
  (its own `Assign` variables, now `RequireResult`), `HoldReleaseExchangeFence`,
  `HoldReleaseWorkflowFence` usage, the store's `$_hold_original_*` and `$_mandate_*` parameters, and
  `SurrealGeneratedStatementScope`. None depend on the old `$_<prefix>_` names.
- `ExchangeAuthorityFence.cs` raw-string brace defect fixed: `RequireResult` plus one `$$"""`
  assertion.
- `SurrealIdempotencyStore.cs`: the blanket Idempotency import was replaced with an alias, and an
  unused `using SurrealForge.Client;` was removed (IDE0005).
- SwapManager mentions removed from `Program.cs` and `IReserveExchangeManager.cs`. Nothing in
  Hold/exchange resolves `SwapManager` or `IDexAdapter`.
- Vocabulary rule: no restricted words in new identifiers, comments, routes or files (scanned).

## Round-4 carry-forward

1. **WTT action resolver (from lane A).** No `wtt` `IActionExternalOperationResolver` exists yet.
   Registering it is one line once lane A authors and reviews the Bridge/Back resolver.
2. **Ethereum/Solana value-custody parity.** Needs the B3-12 amendment accepted, then provider-lane
   implementation. Ethereum needs a reviewed settlement contract.
3. **Direct hold admission UI.** Needs participant liability/reserve-asset read endpoints, and the
   admission is tenant-API-key driven today.
4. **Recipient readiness for direct buys and sales** still uses the synchronous custodial preparer
   before admission. It should move into a durable acceptance stage, the same way Hold does.
5. **Buy-back backstop top-up** (`ReserveToTreasuryBuyBackBackstop.TopUpAsync`) still uses legacy
   `TransferAsync` plus an idempotency anchor, not exact saved wire. It is off by default
   (`Exchange:BuyBack:Enabled=false`).
6. **Retired exchange operation rows** need operator evidence. The reconciler refuses them and logs
   `MANUAL INTERVENTION REQUIRED`.
7. **Independent review** before `IExchangeTransactionModule` is advertised. Until then exchanges fail
   closed.

## Cross-lane edits (additive, own region)

`Program.cs` (exchange/sponsorship comment block), SDK `src/api/client.ts`, `src/api/index.ts`,
`src/index.ts`, `src/api/api-version.ts`, `API_SYNC.md`, `appsettings.json` (`Exchange` keys),
`Core/Surreal/SurrealGeneratedStatementScope.cs` (`RequireResult`), `Core/Idempotency/SurrealIdempotencyStore.cs`,
`Services/Fiat/OnRamp/OnRampRegistration.cs` (stale comment), tests `Exchange/ExchangeRefusalStatusTests.cs`,
`Allocation/AllocationReserveTenantScopeTests.cs`, `Managers/ScopeMapAndReserveDefaultsTests.cs`,
`Fiat/ReversalTests.cs`, `Actions/ActionSigningScopeTests.cs`.

## Requests

- Lane N: prune the unreachable NFT `RefundNodeHandler.HandleAsync` body together with its
  `Tier2ChainNodeHandlerTests` and `ComposedDagDeterminismTests` callers.
- Root: `tests/AZOA.WebAPI.IntegrationTests` has no Moq reference, yet other lanes' integration files use
  Moq. Lane B's integration tests use `InterfaceFake<T>` (`DispatchProxy`) instead.
- Reviewer: independent review of the exchange module before advertising it, plus review of the
  B3-12 amendment.

## Tests cut or declined

Deleted with the retired code: `ExchangeBundleDefinitionTests`, `ExchangeGroupSubmitterTests`,
`ExchangeCompensationTests`, `ReconciledExchangeTests`, `ReserveExchangeManagerTests` (mock-heavy;
replaced by real-store flows), `ExchangeTestHarness`, `ExchangeDrawAuthorityTests`, `PlatformHoldTests`,
the `HoldDrawAnchorTests` class and SDK `hold-view-parity.test.ts`. Declined: per-field validation
variants. The shape refusals collapse into the existing table-driven refusal tests.

## Review fixes (2026-10-16)

Correctness review: REQUEST CHANGES. Parity amendment: ACCEPT WITH CHANGES. Security findings are in
`round3-join-ledger.md` §Lane B findings. Everything is applied in one pass at source level and
nothing has been executed.

| # | Finding | Fix |
| --- | --- | --- |
| H1 | In-batch `Key()` reads were arrays, so every fence threw | `AssignRecord` plus `// lock:` notes in `HoldReleaseExchangeFence.cs`, `HoldReleaseWorkflowFence.cs`, `SurrealHoldCommandStore.cs` and `SurrealHoldReleaseMandateStore.cs`. Consumers rechecked: no leftover `[0]` or `array::len` |
| H2 | Reversal could recover twice or over-recover | The receipt is keyed on (reversal, lot, source, recovered-before), with `OperationId` = the reversal. The request carries the cumulative want plus `ClaimSourceUnits`. The store recovers `want − already`, capped by `claim − allocation.consumed`, with the allocation CAS in the batch. The row records `PurchaseRecoveredBaseUnits`, and rung 0 counts committed-but-unrecorded receipts before rung 1 |
| M3 | Settlement not scoped to the custody tenant | `HoldCustodyScope.AdmitsSettlement` applied in `HoldLocalCommandPlan` (Refund) and `HoldRailAdmissionPlan` (Release, FundedRefund) |
| M4 | A released held purchase could not be recovered | `GetPurchaseLiabilityAsync` returns the release recipient. Provenance raises an `IssuedAsset` clawback capped at the units actually delivered |
| M5 | Recovery rounded down | Hold units are `ceil`, capped at what is held; credit is `min(delta, ceil(share × spent ÷ purchased))`. Provenance shares also round up |
| M6 | Reserve-row contention became a permanent refusal | Bounded re-read and re-plan (`MaxReplans` = 4) in local commit, units commitment, terminal accounting and recovery (`SurrealHoldCommandStore.Contention.cs`) |
| M7 | Race losers did not re-read the winner | `TerminalAsync` re-reads the receipt before every `null`, bound to the operation's terminal state. The recovery loser replays the step at its receipt position |
| M8 | Reversal watermark double-counted in FIFO | `OnRampLot.ReversedBaseUnits` is raised alongside `ConsumedBaseUnits`, and FIFO capacity subtracts only the cash-out part |
| L9 | Previews leaked data; SDK ids weakly validated | The controller runs the consent check on previews that carry `releaseMandate`. The SDK adds `assertHex64` on 16 hold and mandate routes |
| L10 | Pruning | `ExchangeWire` returns `RETURN $_exchange_wire_row` by name. `RequireResult` replaces hand-written guards in Exchange, Units and local commit. `CloseMandate` is shared. Opening settlement reuses `SourceTransition` and `TreasuryMovement` |
| Parity | Amendment changes | Revised to **accepted-with-changes**: Ethereum absence only from a different finalized transaction at the same nonce; the cancel as its own coordinator step; a nonce journal aligned with lane C; Permit2 witness rules; Solana bans durable nonces and lookup tables, with finalized-height absence; ATA rent as a capped nonrefundable cost; rollup L1 data fee or L1 only. §3 is accepted as a direction only |

**Fixtures added or changed:**
- `SurrealHoldCommandTests.ExchangeRecovery.cs`: repeated and changed passes stay within the
  attributed share; a concurrent duplicate commits one step; round-up theories (3/10 with three
  1-unit reversals, 1000/1 partial); the released-purchase recipient; three concurrent commitments
  on one reserve row all commit.
- `HoldLocalCommandPlanTests` and `HoldRailAdmissionPlanTests`: another tenant cannot refund, release
  or funded-refund.
- `ReversalTests`: a lost row save is counted once and never taken again.
- `ReversedPurchaseProvenanceTests`: a released purchase is clawed back from its recipient; a
  partially reversed lot keeps its place.
- SDK `client.hold-commands.test.ts`: non-hex ids are refused before any request.

**Round-4 carry-forward, added:**
1. The `IssuedAsset` clawback rung still uses the legacy amount-keyed idempotency claim, so the same
   over-recovery class remains there until a durable clawback journal exists.
2. `MarkLotConsumedAsync` re-adds the cumulative target if a pass repeats after the lot save. FIFO is
   now immune; the cash-out watermark only errs conservative.
3. New POCO fields (`OnRampLot.ReversedBaseUnits`, `FiatReversal.PurchaseRecoveredBaseUnits`) need
   `.surql` regeneration in the sweep.
4. Hand-written `IF … = NONE OR = []` guards remain in rail partials this pass did not touch.

## Review fixes, cycle 2 (2026-10-16)

Re-review cycle 1 passed 8 of 11 items. These three money-correctness items are closed at source,
and the clawback carry-forward is withdrawn.

| # | Finding | Fix |
| --- | --- | --- |
| 1 (HIGH) | Clawback over-recovery after a lost row save | **Balance:** the key is `(reversal, "balance", RecoveredQuantity − PurchaseRecovered)`. The completed claim stores the exact amount, and a pass replays its own key before re-reading the balance (`ReversalCompensationService.RecoverFromBalanceAsync`, `ReplayRecord`). **Asset:** `ReserveExchangeClawbackAsync` commits a `FiatReversalClawback` receipt (`OperationId` = reversal, id from reversal + lot + source + recovered-before) with the allocation cap and a source-row lock, before anything is sent. The receipt id is the claim key, and unfinished reservations are finished before anything new is reserved. Rung 0 counts a clawback once its claim has completed |
| 2 (HIGH) | Asset rung clawed the whole-lot share but credited only the outstanding part | The store reserves `min(ceil(received × delta ÷ spent), delivered − clawed across the purchase)`, and credit is derived from what was reserved. The ladder sends the reserved units, never `claim.AssetQuantityBaseUnits` (`SurrealHoldCommandStore.ExchangeClawback.cs`) |
| 3 (MEDIUM) | Watermark double-add | `OnRampLot.ReversalMarksJson` records each reversal's marked target in the same lot write, so a pass adds only `target − marked` (`MarkLotConsumedAsync`) |

**Fixtures:**
- `ReversalTests`:
  - a lost save plus a target raised by redelivery claws the balance 30000 then 30000, never 60000 at once;
  - a lost save claws a purchased asset once;
  - 30 percent then 30 percent refunds of a 1000-unit direct buy claw 300 then 300;
  - refunds 30 + 30 plus a repeated pass leave 40000 cashable.
- `SurrealHoldCommandTests.ExchangeRecovery.cs`:
  - direct-buy clawbacks reserve 300, then 300; a replay reserves nothing; another reversal gets the
    remaining 400; nothing beyond that; `ClawedAssetUnits` ends at 1000;
  - a released purchase reserves at most the 400 delivered.
- The harness balance now falls as units are clawed back.

**Still owed:** `.surql` regeneration for the new POCO fields `OnRampLot.ReversalMarksJson` and
`ExchangePurchaseAllocation.ClawedAssetUnits`. A completed claim whose payload has no amount parks
the reversal for a person; nothing in this pre-launch tree is in that state.
