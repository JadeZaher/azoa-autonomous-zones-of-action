---
type: lane-checkpoint
round: 4
lane: fix-money2
branch: r4/fix-money2
base: 75b23986
status: ready (partial scope; D-10 + R2-3 deferred post-QA by orchestrator directive)
---

# Round 4 · fix-money2 lane record

Orchestrator directive mid-lane (owner wants merge + push for live QA): finish the current item, compile clean,
targeted tests green, D-10 pieces not finished become explicit post-QA deferrals, never a half-wired provenance path.
**No D-10 code was written** (design only), so the fix-money floor rule stays in force unchanged.

## DONE

| Item | Fix | Files |
|---|---|---|
| **L6 HIGH** reversal lot link | `FiatReversalEventHandler.cs:240` wrote the stripped `[Id]` into `record<on_ramp_lot>` → every reversal of a credited lot failed to record. Now `SurrealLink.ToLink(OnRampLot.SchemaNameConst, lot.Id)`. Downstream consumers compare by tail (`ExchangePurchaseProvenance.SameRecord`) or carry the value opaquely (allocation hash, saga payload); `MarkLotConsumedAsync` reads by intent. No stored rows carry the bare form (the write never succeeded). | `Services/Fiat/Reversal/FiatReversalEventHandler.cs`; test `ReversalTests.A_reversal_of_a_credited_lot_links_the_lot_as_a_record_not_a_bare_id` |
| **L6 MEDIUM** liability visibility | Decided rule: *what is owed to you is yours to see* — a login caller sees liabilities it is the beneficiary of whatever tenant holds custody; the sending side (owned, not owed) under tenant T is visible only under T's credential. Code already implemented this; the integration fixture was drift. Fixture rewritten (owed-under-tenant visible, sent-under-tenant hidden from login, other-tenant hidden under acting tenant) + a runnable table test of `Visible`. | `Services/Hold/AGENTS.md` §liability-reads; `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealHoldCommandTests.Liabilities.cs`; new `tests/AZOA.WebAPI.Tests/Hold/HoldLiabilityVisibilityTests.cs` |
| **RV-L1 R-1** environment | Payout admission key is now `{user:N}:payout-account:{test\|live}` — one setup per person per payment environment. A test↔live switch finds no row, so resolve cannot list with the wrong credentials and release a claim whose account exists in the other environment. Legacy suffix-less rows unreachable (D-9 fresh DB). | `Services/Fiat/OffRamp/PayoutAccountService.cs`; tests `PayoutAccountCreationFenceTests` (new `A_test_to_live_switch_never_releases_…`), `StripeBankPayoutGatewayTests` |
| **RV-L1 R-2** timeout margin | `FiatOptions.EffectiveRequestTimeout` clamps `RequestTimeoutSeconds` to 1..120 s (used by `CredentialsFor` and `ResolveEndpoint`); the absence margin is `PayoutAccountService.AbsenceMargin` = max(5 min, 2×timeout + 1 min). | `Providers/Fiat/FiatRailProvider.cs`, `Services/Fiat/OffRamp/PayoutAccountService.cs` |
| **RV-L1 R-3** listing scale | `StripeConnectGateway.MaxAccountPages` 20 → 100 (10,000 accounts / 25 h window); beyond that the resolve refuses (incomplete ≠ absent), documented with the scaling path. | `Providers/Fiat/StripeConnectGateway.cs`, `Services/Fiat/OffRamp/AGENTS.md` §account-recovery |

## NOT DONE — explicit post-QA deferrals

### D-10 provenance on transfer (whole item)
Floor rule (`OffRampService.Funding.cs` §arbitrage-cap) remains the only cap for lot-less units; RV-L3 R2-1/R2-2
stay open (owner-confirmable residuals as before). Design worked out for the follow-up:

- **Store:** new `unit_provenance_lot` (holder, from, env, network, chain, asset, `pricing_scope_key`, sold
  `units_per_minor_unit`, currency, inherited `reversal_window_ends`, `source_id`, `transfer_key`, quantity,
  consumed, version); record id = hash(transfer key, source bucket) for replay safety.
- **Transfer:** after the chain transfer *confirms*, debit the sender's buckets (own `on_ramp_lot.consumed_base_units`
  + inherited buckets) lowest-priced first (unknown scope → tenant by rate desc → node) and create the recipient's
  inherited buckets in **one** SurrealDB transaction with version CAS per debited row (`TenantPayoutLedger.CommitAsync`
  pattern). Inheriting the reversal window also closes the transfer-around-the-chargeback-hold hole.
- **Reconcile (chain truth):** buckets vs on-chain holding minus `Requested` cash-outs; a deficit is written off
  matured-before-immature, least-restrictive price first (node → highest-priced tenant → unknown), persisted only in
  mutating flows. Collect leg re-reads holding before moving; short → `Refused` + new `fiat_payout.outcome_code`
  (`option<string>`, e.g. `UNITS_NOT_HELD`), lots and tenant debit released, never paid.
- **Cap:** node scope uncapped; tenant scope with rate capped at it; unknown scope / no provenance → existing floor.
  Allocation from node authority stamps node-scope provenance (removes tenant griefing from earned units).
- **Transfer-path inventory:** user transfers = `BlockchainOperationManager.ExecuteTransferAsync` (+ WalletManager
  callers) — owned by other fixers, needs a post-confirm `RecordTransferAsync` call; allocation =
  `AllocationManager.IssueUnitsAsync` (reserve → user, stamp issuance); Hold = rail completion
  (`SurrealHoldCommandStore.Completion`, funder-in is user→user); exchange = `ReserveExchangeManager` (sell = outflow,
  covered by reconcile; buy = issuance-like, no fiat price); payouts = `OffRampService.ConsumeLotsAsync`.
- Tests owed: transfer-then-cash-out capped at inherited price; mixed provenance; units moved off-platform → flow
  invalid; griefing tenant does not affect other users' provenance-carrying units.

### RV-L3 R2-3 (LOW) floor-scan index/limit
Not started (needs an `on_ramp_lot` `created_at` index + schema regeneration + a fail-closed limit on
`SurrealOnRampLotLedger.ListSoldSinceAsync`). Deferred with D-10 since D-10 shrinks what the floor scan serves.

## Verification
- `dotnet build azoa.sln -c Debug`: **0 errors, 17 warnings** (≤ 25 baseline).
- `dotnet test tests/AZOA.WebAPI.Tests … --filter "ReversalTests|HoldLiabilityVisibilityTests|PayoutAccountCreationFenceTests|StripeBankPayoutGatewayTests|OffRampTests|StripeConnectGateway"`: **136 passed, 0 failed**.
- Not run (rules): integration tests — `SurrealHoldCommandTests.Liabilities` fixture change needs the SurrealDB sweep.
- No schema change in this lane (no POCO edited), so no regeneration was needed.

## Self-review
- Transaction scope: no new multi-row writes. Admission-key change is a pure key derivation; release/attach CAS paths unchanged.
- Money security: the reversal link fix only makes the record writable (the ledger was failing closed). R-1 removes a
  path that could open a second Connect account; R-2/R-3 are fail-closed tightenings. Liability rule widens nothing
  beyond what the code already returned.
- Tests: each new assertion goes red if the line it covers is reverted (bare id; old key finds the test row from live
  mode and lists; `Visible` table rows for both arms). No tests cut.

## Hunks
None in `Program.cs` or configuration.

## Needs other lane
- D-10 follow-up (above) needs a post-confirmation hook in `Managers/BlockchainOperationManager.cs`
  `ExecuteTransferAsync` (owned by another fixer) once the provenance service exists.
