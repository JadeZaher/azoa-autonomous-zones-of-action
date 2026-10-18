---
type: lane-checkpoint
round: 4
wave: W2
lane: L3
branch: r4/money
status: merged
merged: f6b9e04a
merged_d8: 9f484c6b
---

# Round 4 · L3 money lane (`r4/money`, merge #5)

Base `feat/platform-stub-closeout` @ `beae947`. Closing vocabulary: everything below is **Built**
(compiled; the named unit tests were run by filter, as the W2 brief allows for owned tests). No
integration test, container or E2E ran. Helper `r4/money-hold` (Hold/exchange) — see §Helper.

## Scope closed

| Item | Where |
|---|---|
| **D-8 design** (gated; not implemented) | `Services/Fiat/OffRamp/AGENTS.md` §tenant-funded, commit `39df5b7` (`r4(money): d8-design`) |
| D2 inbox record links (SQL-08) | `Services/Fiat/StripeWebhookInbox.cs:467-472` (`hosted_session:`/`user:` links) |
| D2 on-ramp lot claim + operation link (SQL-08) | `Services/Fiat/OnRamp/OnRampSettlementHandler.cs:548-550`, `:436-437` |
| D2 cash-out claim (SQL-08) | `Services/Fiat/OffRamp/OffRampService.cs:336-338`; Stripe metadata re-bared at `:625`, `BankPayoutProcessor.cs:61` |
| SQL-06 price-schedule links | `OffRampService.cs:344`, `OnRampSettlementHandler.cs:352` |
| SQL-06 payout reversal id | `Services/Fiat/OffRamp/PayoutReversalEventHandler.cs:162,175` (write link, compare bare) |
| SQL-07 flag-clear operator link | `Services/Fiat/Reversal/FiatReversalOperatorController.cs:219` |
| SQL-03 price schedule writes + read projection, same change | `Services/Fiat/TenantPriceScheduleStore.cs:135-148` (links), `TryCreateAsync` → `SurrealWriter.Create`, projection `SurrealRecordGuid.ParseOptional(row.TenantId)` |
| Masked readers of the now-linked columns | `OnRampLotLedger.GetByIntentAsync` normalizes a link (`ReversalCompensationService:MarkLotConsumedAsync` passed `hosted_session:…`); reversal subject parses via `SurrealLink.FromLink` (5 sites); operator views project bare ids (`FiatEventOperatorController`, `FiatReversalView`, `FiatPayoutView`) |
| D8 `expires_at` pinned per claim | `Providers/Fiat/FiatRailProvider.cs` `PinnedCheckoutExpiry` + short-lifetime refusal; `Providers/Fiat/AGENTS.md` §expiry-pin |
| F-M2 Connect signing secret | `Controllers/StripeWebhookController.cs` (platform then Connect secret; distinct secret bound to `account`-bearing events); `FiatEnvironmentOptions.ConnectWebhookSecret`; `Providers/Fiat/AGENTS.md` §webhook-secrets |
| F-H8 payout setup first-party only | `Controllers/FiatPayoutController.cs` `BeginAccountSetup` (tenant → 403) |
| F-H8 checkout intents | admission key `{subject}:{tenant|self}:{key}` (`FiatCheckoutController.BuildAdmissionKey`); `FiatRailProvider.DrivenBy` on resume and `GetIntentAsync` (new `tenantId` param) |
| F-H8 payout history / replay | `OffRampService.ListForUserAsync(…, tenantId)` filters to driven rows; non-created claim honoured only when `DroveBy` |
| LOW `FiatEventRecovery.cs:24-37` | sweep cap `MaxAutomaticAttempts = 60`; `Services/Fiat/AGENTS.md` §recovery-cap |
| LOW `OnRampRecovery.cs:136-146` | past 23 h: payment refund list (`IStripeRefundGateway.ListRefundsForPaymentAsync`) adopts ours / opens one new attempt / refuses; `OnRamp/AGENTS.md` §refund-window |
| Clawback MUST-FIX (A-2) ×3 | `ReversalCompensationService.cs` rung 0 pending exclusion, capped pending finish, attempt-suffixed keys (`MoveAttemptKey`, `ResolveMoveSlotAsync`); `Reversal/AGENTS.md` §attempt-keys |
| Reversal `NeedsOperator` exit | `ReversalCompensationService.Resolution.cs` `ResolveParkedAsync` (chain confirmation of the recorded move), route `POST api/admin/fiat-reversals/{id}/resolve` |
| Payout `NeedsOperator`/`Reversed` exits | `OffRampService.Resolution.cs` `ResolveForOperatorAsync` (record-paid / return-units on Stripe transfer-group, bank payout and chain evidence), route `POST api/admin/fiat-payouts/{id}/resolve`; `PayoutAudit.Append`; `OffRamp/AGENTS.md` §operator-resolution |
| Pre-effect refusal → no-effect proof | `Managers/AllocationManager.cs:228,237` (fee obligation, wallet) → `FailBeforeValueEffectAsync`; the op-error site (`:269`) stays plain `FailAsync` (broadcast may have started) |
| Node-fee recovery wired | `Services/Governance/NodeFeeSettlementRecoveryWorker.cs` (hosted; reconcile then defer), `Program.cs` hunk below; `Services/Governance/AGENTS.md` §node-fee-recovery |
| Idempotency `InProgress` after crash (pay leg) | `OffRampService.SendMoneyAsync` replays a stale (5 min–23 h) pay claim under the same Stripe key; `OffRamp/AGENTS.md` §stale-pay-claim |
| `FungibleCreationController` authz review | owner/tenant scoping and signing scopes are enforced (controller `HasSigningScope`, service `BindDirectAsync`/`BindConsentAsync`, store actor/tenant match); added `[EnableRateLimiting("financial")]` on the six POSTs that quote/consent/move |
| W0 D1 re-verified | code path reviewed (`FiatHostedCreation`); new fixture `tests/AZOA.WebAPI.Tests/Fiat/CheckoutCreationFenceTests.cs` + helper B's `PayoutAccountCreationFenceTests.cs` |

## D-8 — tenant-funded payouts (phase 2, built after merge #5)

Critic verdict ACCEPT-WITH-CHANGES; all seven required changes and owner decisions (a)–(c) applied.
Full design + as-built: `Services/Fiat/OffRamp/AGENTS.md` §tenant-funded. Base `ca756013`.

| Item | Where |
|---|---|
| Float / credit / debit POCOs (new tables) | `Persistence/SurrealDb/Models/TenantPayoutFloat.cs`; generated `tenant_payout_{float,credit,debit}.surql` |
| Ledger (one `BEGIN…COMMIT` per transition: float version CAS + entry CAS/create, THROW) | `Services/Fiat/OffRamp/TenantPayoutLedger.cs` `CommitAsync`, `ReadSnapshotAsync` (one-transaction read) |
| Reserve / sync (finalize, release, reversal) / credit promotion + reversal / views | `Services/Fiat/OffRamp/TenantPayoutFunding.cs` |
| Rule 1: release only on proof of no transfer; 409/`idempotency_error` → busy | `TenantPayoutFunding.ReadTransferEvidenceAsync`/`Plan`; `Providers/Fiat/StripeConnectGateway.cs` error mapping |
| Rule 2: `reversed = min(a, max(stored, amount_reversed))` in Reserved and Finalized | `TenantPayoutFunding.Plan`; `PayoutReversalEventHandler.FundingSyncedAsync`; `OffRampService.RecoverFailedBankAsync` sync |
| Rule 3: credit `WHERE state=$from` + float CAS atomically | `TenantPayoutLedger.CommitAsync` (`UpdateCredit`) |
| Rule 4: stranded `Reserved` sweep; never-started cash-out fenced + abandoned | `Services/Fiat/OffRamp/TenantPayoutSweep.cs` (+ hosted worker in `AddFiatOffRamp`) |
| Rule 5: who pays = who priced, re-derived at send | `OffRampService.Funding.cs` `RefuseUnfundedSendAsync`; `funding_tenant_id` set from `OffRampContext` at claim |
| Rule 6: maturity covers the dispute horizon, deficit separate, consistent from one transaction, tenant bound from the intent | `TenantPayoutTopUp.cs` (`MaturesAt`, binding), `TenantPayoutFunding.DescribeAsync` |
| Rule 7: rejected-alternative rationale corrected | `Services/Fiat/OffRamp/AGENTS.md` §tenant-funded |
| Owner (a) credit = `balance_transaction.net`; dispute fees reverse the credit | `IReversalFactSource.GetPaymentBalanceAsync`; `TenantPayoutTopUpReversalHandler` |
| Owner (b) tenant-attributed reserve partition | `IPlatformReserve.ReattributeReturnAsync` (`PlatformReserveService.cs`), `fiat_payout.collect_partition`, `Services/Reserve/AGENTS.md` §reattribution |
| Owner (c) lot pricing scope + exit cap | `on_ramp_lot.pricing_scope_key` (set in `OnRampSettlementHandler`), `OffRampService.ExceedsSoldPriceAsync` |
| Top-up path | `TenantPayoutBalanceController` (`POST …/top-up`), purpose `tenant-payout-float` (allowlisted), rail KYB gate + server-built admission key (`FiatRailProvider.BeginCheckoutAsync`), on-ramp handler ignores top-ups |
| Real-DB probe | the Surreal ledger (create, replay, coverage, stale CAS loses, wrong-state loses **and rolls back the float bump**, release, credit reversal, snapshot indexes) passed against a throwaway SurrealDB 3.1.4 (`--user 0:0`, :8043, reaped); the probe file was not committed |

## Hunks

```hunk path=Program.cs
// Node-fee settlement recovery: the hosted worker drives atomic-group reconciliation, then
// deferral of the remaining due settlements (Services/Governance/AGENTS.md §node-fee-recovery).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.INodeFeeSettlementStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNodeFeeSettlementStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeFeeSettlementAtomicGroupReconciler,
    AZOA.WebAPI.Services.Governance.NodeFeeSettlementAtomicGroupReconciler>();
builder.Services.AddHostedService<AZOA.WebAPI.Services.Governance.NodeFeeSettlementRecoveryWorker>();
```

```hunk path=appsettings.json
      "WebhookSecret": "",
      "ConnectWebhookSecret": ""
```

```hunk path=docker-compose.dev.yml
      Fiat__Test__ConnectWebhookSecret: "${Fiat__Test__ConnectWebhookSecret:-}"
```

```hunk path=appsettings.json
      "AllowedPurposes": [ "funds-top-up", "tenant-payout-float" ],
```

## POCO deltas

- `FiatReversal.PendingMoveKey` → `fiat_reversal.pending_move_key option<string>` (existing table,
  `option<>`, no CAS reads it → no backfill). Schema regenerated with the project generator
  (`Generated/Schemas/fiat_reversal.surql`, +1 line). Not baselined.
- D-8 (phase 2): new tables `tenant_payout_float` (UNIQUE `(tenant_id, currency_code, environment)`),
  `tenant_payout_credit` (UNIQUE `intent_id`), `tenant_payout_debit` (UNIQUE `payout_id`); new
  `option<>` fields `fiat_payout.funding_tenant_id option<record<user>>`, `fiat_payout.collect_partition`,
  `on_ramp_lot.pricing_scope_key` (no CAS reads them on old rows → no backfill; generation reset applies,
  no forward migrations). Schema regenerated with the project generator.

## Published contracts (for L7)

| Route | Body → result |
|---|---|
| `POST /api/admin/fiat-payouts/{id}/resolve` | `{ resolution: "record-paid" \| "return-units", reason }` → `FiatPayoutView`; 409 with reason when evidence refuses |
| `POST /api/admin/fiat-reversals/{id}/resolve` | `{ reason }` → `FiatReversalView` (now also `pendingMoveKey`); re-queues the unwind |
| `FiatPayoutView.subjectUserId/tenantId/reversalId` | now bare compact ids (were link text when read back) |
| `GET /api/fiat/tenant-payout-balance` (tenant principal; ApiKey needs `tenant:provision`) | → `TenantPayoutFloatView[]` per (currency, environment): credited, reserved, finalized, available, deficit, pending credits, recomputed sums, consistent |
| `POST /api/fiat/tenant-payout-balance/top-up` | `{ requestKey, amountMinorUnits, successUrl?, cancelUrl? }` → `FiatCheckoutIntent` (403 `KYB_REQUIRED` without KYB) |
| `GET /api/admin/tenant-payout-floats/{tenantId}` (Operator) | → `TenantPayoutFloatView[]` |
| Cash-out refusal code | `TENANT_PAYOUT_BALANCE_INSUFFICIENT` (400 body code) when the pricing tenant's float does not cover the amount |

## Tests (owned; run by filter)

- CI failures in owned area (9, `round4-ci-main-e0deb11.md`) → all green:
  `OffRampTests` ×4 (harness fidelity: an unsubmitted prepared transfer observes `Unknown`, not
  `Pending`), `ReversalTests` ×2 (fake reversal ledger now clones by JSON round-trip; the hand-listed
  copy dropped `PurchaseRecoveredBaseUnits`), `FiatEventRecoveryTests` ×1 (dropped an unused `Name`
  setup that `VerifyAll` turned into an interaction assertion).
- **Cut (2):** `OperatorVisibilityTests.EventInbox_AsksForTheDeliveriesNoHandlerFinished` and
  `.LotLedger_AsksForRefusedAndStuck_AndForLotsStrandedAtPending` asserted SQL text produced against a
  mocked executor that returned null — implementation restatement; the board behaviour stays covered
  by the controller tests and `MergeOperatorReads`; the row selection belongs to L6's real-DB gates.
- Adapted to new behaviour: `OnRampSagaTests` link-form assertions; `OnRampRecoveryTests` past-window
  case keeps its "unreadable list → operator reference" meaning.
- Added: `CheckoutCreationFenceTests` (lost response replay = same Stripe session; short pinned
  lifetime → new payment; 6 racers → one session; mutation-checked: un-pinning `expires_at` turns two
  red), `OnRampRecoveryTests` ×2 (adopt listed refund; provably absent → exactly one new attempt),
  `OffRampTests.Another_driver_reusing_a_request_key_…` (F-H8).
- Last filtered run (final tree): Fiat + NodeFee + Fungible unit tests **364/364** green.
- **D-8 phase 2 added:** `OffRampTests.TenantFunded.cs` (13: concurrent reservations vs a balance
  covering one; uncovered refusal with the typed code and no transfer; reservation vs top-up promotion;
  Paid → Finalized then partial + duplicate `transfer.reversed`; crash between reserve and send → one
  transfer; abandon → released once; 409 at send keeps Reserved and parks; partial restitution releases
  only the recovered amount and a repeat adds nothing; promotion vs credit reversal; crash after reserve
  before the saga closed by the sweep with the collect fenced; pre-D-8 tenant-priced row with no debit
  parks; tenant driver with node fallback → node-funded at node price; arbitrage cap theory),
  `TenantPayoutTopUpTests.cs` (3: net credited once and never matures without a window; subject/key
  mismatch credits nothing; refund reverses the pending credit by the gross),
  `StripeBankPayoutGatewayTests` theory (409 / `idempotency_error` → busy; a genuine 4xx → refused).
  Races use a commit barrier on `InMemoryTenantPayoutLedger` (production commit contract) so both
  contenders read before either commits. Mutation-checked (coverage check + `max()` reversal → 4 red).
  Harness: `OffRampTests` is now `partial`; `FixedClock` is settable; the connect mock lists transfers
  by group; the node-fallback default is explicit (`NodeFallback = true`) so pre-existing tenant-driver
  tests keep their node-priced meaning. Final run: Fiat + Exchange + Reserve + NodeFee + Fungible +
  schema goldens/orphans + enum converters **700/700** green.

## Compile result

`dotnet build azoa.sln -c Debug` → **0 errors, 22 warnings** (baseline ≤ 25; none new).

D-8 phase 2 tree: `dotnet build azoa.sln -c Debug` → **0 errors, 13 warnings** (≤ 25; all pre-existing xUnit analyzer notes); with `-p:GenerateDocumentationFile=true` (IDE0005 enforced) → 0 errors. `merge-check.mjs` ok; secret scan clean.

## Checklists

**Transaction scope** (`round3-transaction-scope-review.md`): no new raw batch; the price-schedule
create is now `SurrealWriter.Create` (record id = version lock, unique violation → re-read); every
new decision re-reads the row under a version CAS (`SaveAsync`/`UpdateIfVersionAsync`); resolution keys
are deterministic ids (`{moveKey}:resolution`, attempt suffixes) claimed before effect; no DB
transaction spans a Stripe or chain call; losers re-read (attach, resolution claim).
**Security** (every changed route/service): operator routes keep the `Operator` policy + financial
limiter and record the operator principal and reason; evidence is server-fetched (Stripe transfer
group, bank payout, refund list, chain confirmation), never body-supplied; tenant scope added on
checkout resume/read and payout replay/history; payout onboarding first-party only; Connect secret
scoped to account-bearing events; no secrets or PII logged (ids only).
**§7 classes:** bare ids into `[References]` — closed on every L3 write found (SQL-03/06/07/08 +
lot operation link + reversal flag); no `;` in `Of` literals; no ordinal casts; no positional reads
added; no statements after RETURN; captured-null: projection now parses the tenant link so a tenant
schedule never reads back tenant-less.
**Pruning gate:** one raw `CREATE … CONTENT` removed; shared `PayoutAudit` instead of a second audit
shape; `inheritdoc` on new interface members' implementations; IDE0005 clean (app project gate on);
narrowly-typed catch only in `ResolveParkedAsync` (provider-factory refusal → conflict).

## Helper

`r4/money-hold` @ `9fce0a2` (`r4(money-hold): ready — …`) merged `--no-ff` into `r4/money` as
`7fd3c91`, no conflicts. Record: `round4-lane-money-hold.md` (SQL-04, hold liability reads, folded
recipient readiness, durable buy-back top-up, retired-row resolution, consent-gated previews, D-7
gate, operator-only platform-unit partitions; inventories `RouteAuthorization/L3-hold.json`,
`CrossTenant/L3-hold.json`; its `appsettings.json` hunk is recorded there). Merged tree: solution
0 errors / 22 warnings; Fiat + Exchange + Reserve + NodeFee unit tests 507/507 green. The helper's
`Needs other lane` items are carried below. Lead note from the helper: `AllocationManager` still uses
the synchronous `ICustodialRecipientPreparer` for allocation issuance (out of round-4 scope; recorded).

## Residuals (recorded, not hidden)

- An **InProgress** collect claim or reversal move after a crash (no transaction id recorded) has no
  automated exit; only a by-hand chain reconciliation decides it. The ambiguous (`tx=` recorded) cases
  now resolve on chain evidence.
- `FiatReversalLedger.UpdateIfVersionAsync` and `ClaimAsync` remain raw `CONTENT $_body` writes (they
  rely on SurrealForge promoting `table:id` text in `[References]` columns; L6's bare-id rule covers
  them). Converting to typed writers is a pruning follow-up.
- Node-fee `Prepared` settlements with no accepted atomic group stay uncollected (recovery submits
  nothing by design).
- D-8: a collect whose claim completed but whose row write was lost replays without the reserve draw /
  reattribution (pre-existing for the node partition too); the partition counter is then behind until
  an operator repairs it. A reattribution failure is logged and makes a later unit return from the tenant
  partition fail closed (parks with units owed).
- D-8: the debit sweep acts only on `Reserved` debits older than 30 min with a terminal or never-started
  payout; a Finalized debit's later reversal is applied only through `transfer.reversed` delivery /
  recovery (a lost sync fails the event so it is retried).
- D-8 unit fixtures run over the commit-contract fake; the Surreal store was exercised against a real
  SurrealDB 3.1.4 by a throwaway probe, but real-DB integration fixtures for the ledger are L6/sweep work.

## Needs other lane

- **L7 (D-8):** tenant payout-balance page (GET balance, POST top-up) and operator float view; surface
  `TENANT_PAYOUT_BALANCE_INSUFFICIENT` on cash-out.
- **L6 (D-8):** real-DB fixtures for `SurrealTenantPayoutLedger` (concurrent reserve vs one balance,
  promotion vs reversal) and an architecture rule that every `tenant_payout_*` write goes through
  `CommitAsync`/`RecordCreditAsync`.

- **L2 (merged before me):** D-6 KYB at payout price schedules — call
  `IKybGate.RequireVerifiedTenantAsync(tenant, KybCapability.PayoutPriceSchedule)` in
  `Controllers/TenantPriceScheduleController.cs` `Register` (tenant scope) and in
  `OffRampService.ResolveContextAsync` (tenant schedule). `IKybGate` is not on my base yet; I wire both
  in my merge window once L2's merge lands, before the merge check.
- **L1 (unlisted file):** `Helpers/DevelopmentProviderSecrets.cs:21,42` — add
  `Fiat__Test__ConnectWebhookSecret` to the development secret list beside `Fiat__Test__WebhookSecret`.
- **L7:** operator console buttons for the two `/resolve` routes; `pendingMoveKey` on the reversal
  view; SDK `FiatPayoutView` ids are now bare.
- **L6:** real-DB fixtures for the payout resolution evidence paths and the reversal resolution; the
  bare-id source rule should include `CONTENT $_body` POCO writes (SQL sweep recommendation).
- **From the helper (`round4-lane-money-hold.md` §Needs other lane):** L5 —
  `Providers/Blockchain/Algorand/AlgorandGroupReceipt.cs:64` must treat a null `AssetAmount` as 0
  (blocking real-chain exchange settlement) and declare `IExchangeTransactionModule` only on pinned
  networks; L1 — `Controllers/Admin/ProjectTreasuryController.cs:242` should project the bare tenant id;
  L7 — SDK/console for the helper's five routes; L6 — run the helper's captured-null negatives in the
  sweep.
