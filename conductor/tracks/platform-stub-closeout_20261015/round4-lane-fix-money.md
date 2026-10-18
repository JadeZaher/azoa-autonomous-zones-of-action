---
type: lane-checkpoint
round: 4
wave: fix-batch
lane: fix-money
branch: r4/fix-money
base: feat/platform-stub-closeout@21da6183
status: ready
inputs: [".omc/research/round4-review/RV-L3.md (H1,H2,M1-M5,L1-L3)", "RV-L1.md RV-L1-01", "RV-X.md X-7, X-10, X-11"]
---

# Round 4 · fix batch · money (`r4/fix-money`)

Everything below is **Built**: compiled (`dotnet build azoa.sln --no-incremental`: 0 errors, 17 warnings ≤ 25),
owned unit/flow tests run by filter (580 passed, 0 failed over `Tests.Fiat`, Algorand transact/clawback,
provider conformance/certification, exchange), and every guarded line below **mutation-checked**: the line was
inverted/removed, the named test went red, the line was restored (18/18 killed). No integration test,
container or E2E ran. Not merged; the reviewer re-checks H1/H2 first.

## Findings → fix → test

| id | fix (file:line) | test (mutation-checked) |
|---|---|---|
| **H1** double clawback on an ambiguous broadcast | Provider: only an Algod 4xx (≠408) is a definitive refusal; any other POST failure falls through to the pending poll and returns `PendingConfirmation` + txid (`Providers/Blockchain/Algorand/AlgorandProvider.cs:1916,1932,1956`). Callers: an error carrying `OperationStatus.OutcomeUnknownMarker` (`Models/OperationStatus.cs:60,63`) is recorded ambiguous with the txid the message names, never plain `Failed` (`Services/Fiat/Reversal/ReversalCompensationService.cs:557,569` balance; `:805,813` purchase clawback). `ResolveMoveSlotAsync` opens a new attempt key only after a definitive (pre-broadcast) failure; a legacy outcome-unknown record parks (`:858`, `ReplayRecord` `:908`) | `ReversalTests.An_unacknowledged_clawback_broadcast_parks_and_is_never_clawed_again_under_a_new_attempt` (payer holds 2× owed; exactly one clawback over two passes); `AlgorandProviderTransactTests.Broadcast_failure_is_not_retried_and_is_settled_from_chain_truth`, `A_broadcast_algod_refused_with_a_4xx_is_a_definitive_failure` |
| **H2** arbitrage-cap bypass via lot-less / null-scope units | `Services/Fiat/OffRamp/OffRampService.Funding.cs:74-107` (unaged remainder and null-scope lots → cross-scope floor; unknown floor → refusal), floor `:113` over `IOnRampLotLedger.ListSoldSinceAsync` (`Services/Fiat/OnRamp/OnRampLotLedger.cs:78,184`). Design: `Services/Fiat/OffRamp/AGENTS.md` §arbitrage-cap | `OffRampTests.Units_with_no_lot_cannot_leave_node_funded_above_the_cheapest_cross_scope_sale`, `A_legacy_lot_of_unknown_scope_is_capped_at_the_cross_scope_floor_not_skipped`, `An_unreadable_sold_price_record_refuses_lot_less_units_rather_than_guessing`, `Lot_less_units_at_or_below_the_cross_scope_floor_cash_out_node_funded` |
| **M1** return-units while a move may be in flight | Pay-leg fence before units go back (`OffRampService.Resolution.cs:143`, `FencePayLegAsync` `:206-235`): absent claim → claimed + failed under `OperatorFenceOperationType`; InProgress younger than `PayClaimInFlightHorizon`, Completed-without-transfer, or ambiguous → refuse. Absent collect claim fenced before "not collected" (`ResolveCollectionAsync` `:237-255`). A fence a crash left InProgress is never replayed as a lost payment (`IsReplayablePayClaim` checks the operation type, `:279`). `AbandonAsync` on `Requested` parks while the collect is InProgress/ambiguous (`OffRampService.cs:755`) | `OffRampTests.Returning_units_fences_the_pay_leg_so_a_send_that_read_the_row_earlier_cannot_pay`, `Returning_units_is_refused_while_a_pay_attempt_may_still_be_in_flight`, `Closing_a_never_collected_cash_out_fences_the_collect_leg_against_a_late_collect`, `A_fence_claim_a_crash_left_in_progress_is_never_replayed_as_a_lost_payment` |
| **M2** ambiguous collect treated as never collected | `OffRampService.cs:545-558` (outcome-unknown error → `AmbiguousWithTransaction(txid)` + park); `ClassifyReplay`/`ResolveCollectionAsync` use `IsAmbiguousFailure` | `OffRampTests.A_collect_whose_broadcast_outcome_is_unknown_is_never_closed_as_not_collected` (unknown → refused; chain Confirmed → units returned once; collect never re-sent) |
| **M3** reversal before the top-up credit | `TenantPayoutTopUp.cs:47` `ReadReversedAsync` (refunds + withdrawn dispute incl. fee); credit write seeds `reversed` from the charge (`:126`) and converges an existing credit (`:156`); reversal with no credit → `Failed` (redelivered) when the payment funded a top-up intent (`:199`, `IsTopUpPaymentAsync` `:219`); `PaymentBalanceFacts` gains `AmountRefundedMinorUnits`/`Disputed`/`CreatedAt` (`Services/Fiat/Reversal/StripeReversalFactSource.cs:63`) | `TenantPayoutTopUpTests.A_refund_that_arrives_before_the_credit_stays_pending_and_the_credit_is_written_reversed`, `A_reversal_of_an_ordinary_payment_with_no_credit_is_not_a_top_up_concern` |
| **M4** stranded debits / sweep starvation / "consistent" | `Reversed` finalizes (`TenantPayoutFunding.cs:326`); sweep cursor-pages every stale Reserved debit (`TenantPayoutSweep.cs:35-58`, ledger `ListStaleReservedAsync(before, from, …)` `TenantPayoutLedger.cs`); view adds `StrandedReservedCount`/`OldestReservedAt` and `Consistent` is false past `StrandedReservedAfter` = 7 d (`TenantPayoutFunding.cs:93,246`) | `OffRampTests.A_transfer_reversed_while_bank_pending_finalizes_the_debit_and_returns_the_float`, `The_sweep_reaches_a_crashed_cash_out_behind_more_than_a_page_of_long_lived_debits`, `A_debit_reserved_past_the_threshold_makes_the_float_report_stranded_not_consistent` |
| **M5** fixture fidelity | Harness: Stripe mock is idempotent per key (`CreatedTransfers`), a 409 means the concurrent request created the transfer and the group lists it, `CrashAfterTransferCreated` throws after creation; ledger fake `BeforeCommitOf` gates one commit kind | `A_crash_after_the_transfer_and_before_its_claim_completed_replays_to_exactly_one_transfer` (red with the stale-pay replay disabled); `A_payout_reservation_racing_a_top_up_promotion_lands_both_exactly_once` (only the reserve commit gated; asserts the reserve CAS lost once); `A_409_park_is_recorded_paid_from_the_concurrent_transfer_and_finalizes_the_debit_once_paid` (operator resolve + D-8 sync); `An_operator_return_of_a_collected_tenant_cash_out_releases_its_debit_once`; `One_tenants_float_never_funds_or_drains_another_tenants_cash_out`; plus the Reversed-debit, refund-before-credit and ambiguous-broadcast flows above |
| **L1** partition drift on a Completed collect replay | `CollectPartition` set before the claim (`OffRampService.cs:511`); Completed replay re-applies the replay-windowed draw (`:526`, `ApplyCollectDrawAsync` `:570`) | `OffRampTests.A_collect_replayed_after_a_crash_still_returns_the_units_to_the_funding_tenant` |
| **L2** group membership is the evidence | `OffRampService.Resolution.cs:61` (any non-matching group transfer → refuse); `TenantPayoutFunding.cs:307` (→ Retry) | `OffRampTests.A_group_transfer_that_does_not_name_the_cash_out_blocks_the_resolution_and_the_release` |
| **L3** maturity clock / promotion starvation | `AvailableAt` from the charge's `created` (`TenantPayoutTopUp.cs:148`); `ListMaturedPendingAsync(…, floatId)` filters in the query (`TenantPayoutFunding.cs:266`) | `TenantPayoutTopUpTests.A_late_top_up_delivery_matures_from_the_charge_time_not_the_handler_time` |
| **RV-L1-01** payout-account setup locked out past the replay window | `PayoutAccountService.ResolveStuckSetupAsync` (`Services/Fiat/OffRamp/PayoutAccountService.cs:294-360`): read-only stuck classification; complete Stripe listing over the claim window (`StripeConnectGateway.ListConnectedAccountsCreatedAsync` `Providers/Fiat/StripeConnectGateway.cs:185,407,434`); found → attach via `ReconcileAdmissionAsync` (audited); proven absent past window + 5 min → quarantine (audit) + `ReleaseUnsentAdmissionAsync` so the next setup opens a fresh claim/token/key. Route `POST api/admin/fiat-payouts/accounts/{userId}/setup/resolve` (`FiatPayoutOperatorController.cs:114-131`). `Providers/Fiat/AGENTS.md` §creation-fence corrected | `PayoutAccountCreationFenceTests.An_operator_attaches_the_account_a_lost_create_opened_and_setup_then_continues_on_it`, `An_operator_releases_a_claim_Stripe_proves_absent_and_the_next_setup_opens_one_fresh_account`, `A_setup_still_inside_its_replay_window_is_not_operator_work`, `An_incomplete_Stripe_listing_is_never_taken_as_proof_of_absence` |
| **X-7** step-up at money seams | `[Authorize(Policy = "RecentNodeOperatorSession")]` on every mutating action of `FiatPayoutOperatorController` (advance, return, recover, resolve, park, restart, setup/resolve) and `FiatReversalOperatorController` (recover, settle, resolve, restart, clear-flag), and `ExchangeController.ResolveRetiredRow` (`Controllers/ExchangeController.cs:80`); reads keep `Operator`. Inventories updated | route-inventory rows (`Inventories/RouteAuthorization/L3.json`, `L3-hold.json`) |
| **X-10** stale seam comments | `Program.cs` WTT ownership-proof comment and the Wave-4 composition comment (line references → names); `Services/Actions/ValueRailCapabilityGate.cs` cites `Services/Kyc/KycGateService.cs` | — |
| **X-11** funded default in base config | `appsettings.json` `Exchange:MaximumRecipientFundingBaseUnits` = `"0"`; `"201000"` only in `appsettings.Development.json` and `appsettings.IntegrationTest.json` | — |

## Owner-confirmable decision (H2)

Rule 3 of §arbitrage-cap caps units of no matured lot of known scope (transferred-in, **earned** or gifted, legacy
null scope) at the cheapest non-node price sold in the last `Fiat:ChargebackWindowDays` (120 d). Consequence:
if any tenant sold cheaper than the node in that window, a person cashing out **earned** units node-funded is
refused above that price. When no non-node scope sold in the window the floor is vacuous (the exit proceeds);
an unreadable record or rate refuses. Alternatives if the owner prefers: (a) cap only transferred-in units (needs
a transfer-provenance record that does not exist), (b) widen the window (tighter, caps more), (c) make the
platform unit `DefaultFrozen` for user-to-user moves (closes the transfer at the source; a product decision).

## Hunks

```hunk path=Program.cs
// The wallet ownership challenge's persisted proof (L4, Services/Wormhole/WttRecipientOwnership.cs).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Wtt.IWalletOwnershipProof, WalletOwnershipProofAdapter>();
```

```hunk path=Program.cs
//                             and IRealValueKycGate (the single registration above).
//   AddOnRampValueRateSource  needs AddFiatOnRamp AND AddValueRateSources (above).
//   AddFiatReversalPipeline   needs the on-ramp lot ledger, the reserve, the hold primitive,
//                             the idempotency store, ISagaCoordinator (above), the
```

```hunk path=appsettings.json
    "MaximumRecipientFundingBaseUnits": "0",
```

```hunk path=appsettings.Development.json
  "Exchange": {
    "MaximumRecipientFundingBaseUnits": "201000",
    "Activation": {
```

```hunk path=appsettings.IntegrationTest.json
  "Exchange": { "MaximumRecipientFundingBaseUnits": "201000" },
```

Comment-only Program.cs edits; no registration moved (merge-check: ok).

## Checklists

- **Transaction scope:** no DB transaction spans a Stripe or chain call. Fences are single-record CAS writes
  (claim-if-absent, then fail) ordered after the transfer-group read; a transfer created between the read and the
  fence leaves its claim InProgress/Completed, which refuses. A crash between fence-claim and fence-fail leaves an
  InProgress claim of the fence operation type: it blocks sends and is never stale-replayed. The stuck-setup exit
  does Stripe listing first, then two CAS writes (quarantine with audit, release); a lost second CAS leaves an
  audited quarantined row the operator re-resolves.
- **§7 fail-closed:** unknown sold price → refusal; incomplete/unreadable Stripe listing → no release; outcome-
  unknown broadcast → park; reversal for a top-up with no credit → redelivery, not drop.
- **Security:** new route Operator + `RecentNodeOperatorSession` + financial limiter; actor from claims; reason
  20–1000 chars; the found account must carry this claim id AND this subject. X-7 closes stale-JWT money actions.
- **Pruning gate:** no raw SurrealQL added (typed `SurrealQuery<T>` for `ListSoldSinceAsync`, the paged stale
  read and the float-filtered promotion); no catch-all; interface XML owns contracts; IDE0005 clean.

## Tests authored / changed

Authored: 26 flow tests listed in the table (+ the fence-op-type crash case). Changed: `A_crash_between_reserve_and_send…`
replaced by the real-crash fixture; `A_payout_reservation_racing_a_top_up_promotion…` now gates only the reserve
commit; `Broadcast_failure_is_not_retried_double_spend_guard` now asserts chain-truth settlement (a 5xx is no
longer reported as a failure). `OffRampTests.Lot()` fixtures now carry a real node scope and rate (an issued lot
always has one; the old `"0"` rate is unreadable and would now — correctly — refuse lot-less exits). Declined:
a dedicated mutation of the M3 credit seed alone (two independent defences — the seed and the post-record
convergence — each make the same flow green; mutating both turns it red).

## Residuals (recorded, not hidden)

- Algorand's confirmation classifier never yields `FailedOnChain`, so an ambiguous move that truly never landed
  stays parked until reconciled by hand; an expiry proof needs the transaction's `LastValid`, which
  `BuildSignSubmitCoreAsync` does not return. Other Algorand broadcast sites (atomic groups, recipient
  preparation) and Solana still return an outcome-unknown error; callers honour the marker.
- H2 window/scan residuals and the transfer-defeats-aging hole (pre-existing): see §arbitrage-cap.
- Checkout claims have no Stripe-specific stuck exit (a new request key opens a new claim).
- Pre-existing red architecture gates owned by other lanes (`HostedIdentityCallback.Callback` classified twice in
  L2.json; unclassified identity/admin routes; L2/L3 cross-tenant stale combined rows) were not touched.

## Needs other lane

- **L7 / SDK** `sdk/azoa-wallet/src/api/money.ts` `TenantPayoutFloatView`: add `strandedReservedCount: number`
  and `oldestReservedAt: string | null` (additive JSON fields from `TenantPayoutFunding.cs:36`); the payout-balance
  page should show "stranded" when `consistent` is false.
- **L7 / frontend** admin fiat-payout and fiat-reversal mutations and the exchange retired-row resolve now require
  a fresh operator session (`RecentNodeOperatorSession`, ≤ 10 min): surface the step-up prompt the operator
  console already uses. New route `POST /api/admin/fiat-payouts/accounts/{userId}/setup/resolve` `{ reason }` →
  `PayoutAccountSetupResolution { outcome: "attached" | "released", accountId }` needs an SDK method and a button
  on the account view.
- **L5 / chains** (FYI, edited under the H1 brief): `AlgorandProvider.BuildSignSubmitCoreAsync` broadcast
  classification (`:1910-1935`). Consider the same treatment for the atomic-group and recipient-preparation
  broadcasts and Solana's `Broadcast failed` path.
