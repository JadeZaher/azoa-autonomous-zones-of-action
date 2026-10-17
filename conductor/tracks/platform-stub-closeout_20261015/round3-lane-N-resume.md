---
type: lane-checkpoint
lane: N
status: review-fixes-applied
owner: claude-lane-N
---

# Round 3 lane N (NFT / credential lifecycle) resume checklist

Resumed 2026-10-16 from checkpoint `3d08ace` (Codex rollout `01a13fa5`, cut off 23:04Z
mid-slice). Policy: no build/test/lint/typecheck/schema; static self-review only. Runtime for
every fixture below is NOT RUN. Generated `.surql` for the new `nft_lifecycle` history fields
(`StageConsentsJson`, `ReplacementCostConsentsJson`, `ReplacementCostRetirementsJson`,
`ActorContinuationsJson`) must be regenerated in the final sweep before real-store fixtures run.

## Done before cutoff (source-accepted)

- D1 cost receipt contracts/store (C). D2 admission receipt selection + DB-time expiry (B).
- D3 Solana Create quote/preparation/first-send cost enforcement (C). D4 public offers, SDK, mint form (B).
- D5 QuotedIntentHash pre-admission correction (B). D6 workflow cost consent incl. retirement (B).
- D7 direct original authority for all operations + tombstone/workflow-rejection exemptions (B).
- D8 pinned PlatformAuthorityAddress (B). D9 workflow original witness binding (B).
- D10 participant stage consent + eligible replacement-stage controls (B).
- D11 owner consent management UI and command `Withdrawn` (A accepted; schema regen only).

## Checklist (all closed at source level)

- [x] R1 Replacement cost backend review: typed refusal for missing request/stage/costs bodies
      (`NftLifecycleService.ReplacementCosts.cs:17,62,103`). No other defect found in contract,
      fences, retirement or reconciliation ordering.
- [x] R2 Replacement cost UI: an Applied/Obsolete saved acceptance now permits reviewing a later
      generation (`nft-replacement-costs.tsx:28-29`); request archived before HTTP; reconcile or
      retire required before replacing an unanswered request. SDK `api/index.ts:139-148` now exports
      the stage/replacement/cost types that `src/index.ts` and the browser import (was a TS2305 break).
- [x] R3 Replacement fixtures: `NftReplacementCostTests` (6 flows), `SurrealNftLifecycleStoreTests.ReplacementCosts`
      (7 tests), SDK exact-body test, `nft-replacement-costs.test.tsx` (2 flows). Harness gained an
      optional Solana chain; the in-memory store mirrors durable append/retirement preconditions.
- [x] R4 Cost-selection fixtures owed to B: positive propagation, offer-gone replay + crash resume,
      historical null-omission byte identity (`NftProtocolCostSelectionTests`), real DB-time
      receipt expiry not blocking the admitted generation, later-generation refusal.
- [x] R5 Lane B 22:39Z regression: verified fixed by `NftCancellationTombstone` (fresh-claim-only
      exemption, store `Fenced`); added service-to-store marker parity assertion
      (`NftLifecycleSafetyTests.CancelledKeyFencesAnAlreadySuspendedAdmission`). Admitted/prepared
      operations keep the full authority fence.
- [x] R6 Fixture migration for the direct-authority change: real-store `Candidate()`/`CostCandidate()`
      now seed a real user, wallet and direct witness; admission-safety workflow fixture seeds the node
      signing witness; Moq expressions completed for new optional parameters; NftController,
      Credential issuance, NftManager and credential-revocation fixtures forward a captured witness.
- [x] R7 Refund crash-before-custody resume fixture (holder-only replay).
- [x] R8 Directory AGENTS.md notes (Services/Users, Providers/Stores/Surreal, frontend shared).
- [x] G1 (found on resume) Lapsed direct requester credential stalled every later stage and made a
      confirmed-stage lifecycle uncancellable. Implemented explicit requester renewal following the
      reviewed fungible continuation pattern (`NftActorContinuationIdentity`, store effective fence,
      `RenewActorAuthorityAsync`, `POST /api/usernft/lifecycle/{id}/renew-authority`, SDK, panel) with
      unit, real-store, SDK and panel fixtures. Needs independent adversarial review at the join.

## Review fixes (join ledger §Lane N findings, 2026-10-16)

Source only; runtime NOT RUN.

1. HIGH renewal carried signing fences: `Helpers/NftEffectAuthorities.cs` is now the one selector of fenced
   witnesses per write kind (`NftLifecycleWrite`). Requester or participant renewal-only writes fence only the
   new credential (`SurrealNftLifecycleStore.cs:123-124`, `Fenced` :160). The replacement-cost branch is limited
   to unconfirmed Create generations. Real-store: refund awaiting holder, soulbound gen 1 after confirmed Create,
   renew before cost acceptance.
2. HIGH lapsed participant/cost credential: participant renewal appends a fresher consent for the same stage,
   generation, intent, tenant and wallet while Preparing/Prepared (`NftStageConsentIdentity.cs:21-50`,
   `NftLifecycleService.Authority.cs:38`, accept route `NftLifecycleService.cs:294-301`). `Current` is the latest
   entry. The cost acceptance is immutable evidence fenced at its own commit; later steps fence the current
   requester. Unit + real-store participant-lapse fixtures; first send after acceptor lapse succeeds.
3. MEDIUM retries: every lifecycle batch (claim, update, cancel, commit, reserve) runs through
   `SurrealTransientConflict.RetryOnConflictAsync` (`SurrealNftLifecycleStore.cs:209`).
4. MEDIUM cross-tenant: `RequesterOwns`/`Visible` (`NftLifecycleService.Discovery.cs:42-53`) scope get, refresh,
   list and cancel. Admitted cancel needs the requester credential, `BindConsentAsync` and `ForConsent`
   (`NftLifecycleService.cs:544`, store :265-271). Controllers pass tenant and credential. `NftTenantScopeTests`.
5. Renewal rule follows the fungible precedent (any live direct credential of the same user, tenant and wallet;
   it supersedes a revoked original). Documented in Services/Users/AGENTS.md; cancellation claim corrected;
   real-store theory renamed with an `original-revoked-then-renewed` case.
6. SDK `assertLifecycleId` on all 10 lifecycle-ID URLs (`client.ts:2058`); panel validates typed references.
7. In-memory store mirrors witness selection and direct expiry (shared selector + `Now`); renewal coverage moved
   to the real store; SDK echo assertions removed; revoke-between-bind-and-commit theory (JWT, API key, tenant grant).
8. Named result `$_nft_effect_row` replaces the guard-count offset (`SurrealNftLifecycleStore.cs:201-203`).

Re-review cycle 1 approved; residual LOWs closed (source only): refund source and source fee quote use
`RequesterOwns` under the caller tenant; participant renewal requires the superseded credential to have lapsed
or been revoked (store batch guard `NFT_PARTICIPANT_CONSENT_LIVE`, in-memory mirror for expiry); store doc now
names the effective requester. Fixtures: cross-tenant refund/quote refusal; live re-accept refused (unit and real store).

## Round-4 carry-forward

- G2 Solana recipient-stage costs: recipient Accept (recipient-paid ATA rent + fee) and Transfer
  (sender-paid destination ATA rent) are not separately quoted. The reviewed round-3 protocol-cost
  amendment scoped implementation to metadata-bearing Create; a recipient-authorized quote contract
  (existing-ATA zero-cost case, payer roles, Algorand opt-in minimum-balance parity) needs C's provider
  review before code, which is round-4 plan work.

## Cross-lane edits

- `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealActionAdmissionSafetyTests.cs` (seed original workflow witness).
- `tests/AZOA.WebAPI.Tests/Actions/Handlers/Tier2ChainNodeHandlerTests.cs`, `Actions/ComposedDagDeterminismTests.cs`,
  `Actions/Handlers/ActionNodeHandlerTests.cs` (Moq arity only).
- Hot shared, additive in NFT region: `sdk/azoa-wallet/src/api/client.ts`, `sdk/azoa-wallet/src/api/index.ts`;
  shared doc `Providers/Stores/Surreal/AGENTS.md`.

## Requests to other lanes

- B: check Hold participant stages for the same lapsed-original-direct-witness stall (NFT now renews explicitly).
  `RefundNodeHandler.HandleAsync` is unreachable in production (registry wraps Refund) and refuses without
  authority if called; consider removing it with its unit tests.
- C: round-4 review of the G2 recipient-stage cost contract.
