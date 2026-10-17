---
type: lane-checkpoint
lane: L1-helperB
branch: r4/core-fiat
base: e075b6d
status: ready
---

# Round 4 — L1 helper B (fiat subtree) lane record

Scope: plan §4 L1 **Helper B** — D1, F-M5, `OffRampService` `requestedQuantity`, and every
compile/SRDB0001/IDE0005 error in `Providers/Fiat/**`,
`Services/Fiat/OffRamp/{PayoutAccountService,OffRampService}.cs`,
`Providers/Stores/Surreal/SurrealHostedSessionStore.cs`, `Interfaces/HostedSessions/IHostedSessionStore.cs`
and owned tests.

## D1 — creation fence for Stripe-backed hosted claims

Shared helper `Providers/Fiat/FiatHostedCreation.cs` (`BeginOrResumeAsync`, `IdempotencyKey`,
`ReplayWindow`); rationale in `Providers/Fiat/AGENTS.md` §creation-fence.

Ordering (both checkout and payout account):

1. **Claim with a `CreationToken`** — `FiatRailProvider.cs:523-524` (checkout candidate),
   `PayoutAccountService.cs:187-188` (payout candidate). Create-if-absent on the admission key
   means concurrent requests converge on one row.
2. **Adopt the row's token and fence before Stripe** — `FiatHostedCreation.cs:45-77`:
   attached → reuse; not `CLAIMED` / tokenless / quarantined → `NeedsOperator`; begun within 23 h →
   `Send` (replay); otherwise `TryBeginCreationAsync` (version CAS on `creation_started_at = NONE`,
   exactly one winner); a loser re-reads once and re-decides.
3. **Stripe create with a claim-bound idempotency key** `<prefix><rowId>-<token>` —
   `FiatRailProvider.cs:608` (checkout), `PayoutAccountService.cs:328` (connected account; also
   `metadata[azoa_payout_claim]`, `StripeConnectGateway.cs:222-223`). Every replay of one claim sends
   the same key, so Stripe returns the object it already created or 409s a concurrent duplicate.
4. **Attach with the token, persist the id** — `FiatRailProvider.cs:631-638`,
   `PayoutAccountService.cs:339-348` (`provider_session_id` = connected-account id). Attach loser
   re-reads and accepts only the identical Stripe id (`FiatRailProvider.cs:642-646`,
   `PayoutAccountService.cs:353-362`).

**Crash safety.** Crash before 2 → next request begins the fence. Crash/lost response between 2
and 4 → next request finds `creation_started_at` set and replays the same key, then attaches.
After 4 → reused. No DB transaction spans a Stripe call. Past Stripe's ≥24 h key retention
(23 h window from `creation_started_at`) the verdict is `NeedsOperator` (operator
`ReconcileAdmissionAsync` attaches the verified object, findable by `azoa_payout_claim`), never a
second create. **Wrong-claim attach** is impossible: the attach predicate requires the claim's
token + begun creation + no provider id + version, and the id attached came from that claim's key.

Also: `PayoutAccountService.FindAccountIdAsync` (`:278-294`) now reads with `GetByAdmissionAsync`
instead of the create-if-absent claim, so status reads no longer leave tokenless admission rows
(which would have wedged later setup).

## F-M5 — attach honours `expectedStateVersion`

`SurrealHostedSessionStore.cs:228-251`: the caller's version is a real CAS again. The one tolerated
intervening write is an operator **quarantine** (`creation_quarantined_at` set), which a late
original provider response is designed to resolve (tested:
`HostedSessionReconciliationTests.SentRequest_AbsenceQuarantinesUntilOriginalResponseAttaches`,
`SurrealHostedSessionRecoveryTests.QuarantinePreservesAdmissionAndOriginalCreatorCanAttachBothAliases`).
The WHERE pins the freshly observed version so read→write still serialises. Contract text updated
(`IHostedSessionStore.cs:50-63`); the in-memory store mirrors it
(`InMemoryHostedSessionStore.cs:123-126`). Callers verified: `HostedSessionFlow.cs:176` passes the
post-begin version; `HostedIdentityRecovery.cs:86` re-reads first.

Same file: SRDB0001 at the old `:375` → `SurrealGeneratedStatementScope.Assign` + `RequireResult`
(also scopes the mutation's parameters so they cannot merge with alias-fence bindings); the
`RETURN $_attached` result is read by **absolute slot** (`:381-391`) because a top-level RETURN
drops later statements from the result array (round-4 optimism assessment) — the old
`Statements.Count - 2` would then point at an alias guard. Null-safe custodial subject check
(`:114-116`, CS8604). Unused `using AZOA.WebAPI.Helpers` removed (global using).

## `requestedQuantity` decision

`OffRampService.cs:452` — removed the undeclared override; collect moves exactly
`row.QuantityBaseUnits`. History: `3d08ace` added optional `requestedQuantity` only to
`ReturnUnitsAsync` (partial restitution via `RestoreReservedUnitsAsync`); the collect line was a
copy of that edit. An override at collect would bypass the availability check done in
`RequestAsync`. Recorded in `Services/Fiat/OffRamp/AGENTS.md` §collect-quantity.

## Test changes

- **Added** `tests/AZOA.WebAPI.Tests/Fiat/PayoutAccountCreationFenceTests.cs` (authored, not run;
  compiles against the merged `r4/core` tip): through `PayoutAccountService` with the real
  in-memory store and a Stripe fake that models idempotency (same key → same account, concurrent
  same key → 409, new key → new account):
  in-flight race (5 racers while the first create is held) → exactly one account, row attached to
  it, every onboarding link names it; cold 8-way race → one account; lost create response →
  replay reuses the key and attaches the same account; lost response past the window → `Conflict`,
  no second account; status read claims nothing and setup still succeeds. Mutation check: keying by
  the request's token, skipping the fence, or a non-claim-bound key each opens a second account.
- **Adapted** `StripeBankPayoutGatewayTests.cs:26-28` — status lookup mock moved from
  `ClaimAdmissionAsync` to `GetByAdmissionAsync`; `:82-84` raw-string `CS9007` (four consecutive
  closing braces under `$$`) fixed by spacing the JSON braces.
- **Adapted** `InMemoryHostedSessionStore.cs` attach to the F-M5 CAS.
- No deletions. No exactly-once assertion weakened.

## Checklists

**Transaction scope** (`SurrealHostedSessionStore` attach/begin, creation fence): attach = typed CAS
(version + state + token + started + no provider id) and alias reservations in one
`BEGIN…COMMIT` with `THROW` guards; deterministic alias ids are the lock; begin-creation is a
version CAS; no retry wrapper added — losers re-read the winner; no DB transaction spans Stripe;
result slot is absolute, not end-relative.
**Security**: subject ownership checked before the fence (checkout `OwnedBy`, payout subject
match); idempotency/replay claim-bound; logs carry internal row ids only (no account ids, keys or
PII added); subject/tenant ids reach `record<user>` columns only through `ToStorage`
(`SurrealLink.ToLink`).
**§7 fail-closed classes**: binding names scoped via `Assign`; no array captures, positional offsets
fixed, SQL-declared `$_attached` only; no raw lowercase-enum / explicit-NULL writes; no bare ids into
`[References]`; no `;` in `Of` literals; no ordinal casts; RETURN is directly before COMMIT.
**Pruning gate**: no new raw SurrealQL; no catch-alls; shared fence helper instead of two copies;
stale "no read-by-admission-key" comment removed; IDE0005 clean in owned files (checked with
`-p:GenerateDocumentationFile=true`).

## Needs outside subtree

- **L3 / D8** (`FiatRailProvider.cs:591` `expiresAt` recomputed per attempt): a checkout replay after
  a lost response now reuses the claim key, so Stripe answers `idempotency_error` and the user sees
  busy (fail-closed, no second page). Pin `expires_at` at the first claim/begin as planned.
- **Lead (IDE0005, outside my paths)**: with `GenerateDocumentationFile=true` the merged tree reports
  ~625 IDE0005 errors elsewhere (incl. fiat files owned by L3 in W2: `Controllers/FiatPayoutController.cs:4`,
  `Persistence/SurrealDb/Models/{FiatEvent,FiatPayout,FiatReversal,HostedSession}.cs`,
  `Services/Fiat/{FiatEventRecovery,TenantPriceScheduleStore}.cs`,
  `Services/Fiat/OffRamp/BankPayoutReconciliationWorker.cs:3`, `Services/Fiat/Reversal/{FiatReversalLedger,OnRampLotLookup}.cs`,
  `Services/HostedSessions/HostedIdentityRecovery.cs:1`). The default build does not run IDE0005
  (`EnableGenerateDocumentationFile` warning), so the csproj's IDE0005-as-error gate is currently inert.
- `Providers/Stores/Surreal/SurrealHostedProviderAttemptStore.cs:137,146` (`.Build()`): lead's list, not mine.

## Compile result

- `r4/core-fiat` alone: `dotnet build azoa.sln -c Debug` → **0 errors and 0 warnings in owned paths**
  (113 errors elsewhere, all lead/helper-A territory on the `e075b6d` base).
- Scratch merge of `r4/core` (`5b818c2`) + this branch (detached worktree, discarded): main project
  compiles; `AZOA.WebAPI.Tests` reports 27 semantic errors, none in owned files; integration tests 4
  errors (`ISurrealExecutor` usings), none in owned files. No tests run.
