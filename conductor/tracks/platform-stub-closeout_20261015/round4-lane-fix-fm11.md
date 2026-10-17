---
type: lane-checkpoint
round: 4
lane: fix-fm11
branch: r4/fix-fm11
base: 21da6183
findings: [F-M11, RV-L5-09, RV-L4-02, RV-L1-06, RV-L1-05]
merged: pending
---

# Round 4 fix batch: r4/fix-fm11 (NFT lifecycle)

Scope: F-M11 (recipient-stage Solana costs), plus orchestrator additions RV-L5-09 (takeover vs stale run),
RV-L4-02 HIGH (NFT recipient resolution after M2), RV-L1-06 (revoke deny-path coverage), RV-L1-05 (gate test).
No Program.cs or config hunks (new controller is attribute-routed; new service dependency resolves from the
existing `IWalletOwnershipProofStore` registration, optional so hosts without it fail closed).

## F-M11 recipient-stage Solana costs

- **Which stages:** `NftStageCostContract.Required` = Solana `Accept`, `Transfer`, `ReturnForBurn` (every stage whose
  message can create the destination ATA). Deliver/Revoke/Burn create no account and stay under the admitted
  requester's original fee consent.
- **Who pays:** non-refund `Accept` → the recipient; otherwise the requester (`Payer`), matching the provider's
  `NftPayer`. Payer tenant = the payer's own stage consent tenant when it signs the stage, else the requester's
  effective tenant (`PayerTenant`).
- **Quote / accept:** `NftLifecycleService.StageCosts.cs` (`QuoteStageCostsAsync`, `AcceptStageCostsAsync`), routes
  `POST /api/nft-stage-costs/{id}/quote|accept` (`Controllers/NftStageCostsController.cs`). Five-minute immutable offer
  (existing `nft_cost_receipt` store) bound to version/state/stage/generation, `DraftHash(row)`, payer wallet,
  physical network and `QuotedIntentHash`. Append-only `NftLifecycle.StageCostConsentsJson` (`option<string>`,
  schema regenerated; generation reset ⇒ no migration).
- **Store fence (same transaction):** new `NftLifecycleWrite.StageCostAcceptance` fences the payer credential +
  `NftProtocolCostAdmissionFence` (receipt immutability, `expires_at > time::now()`) with the version CAS. Preparation
  and first-send writes must pass `NftStageCostContract.AdmitsWrite` (binding acceptance; a prepared wire must carry
  exactly that receipt). In-memory store mirrors both, including DB-time expiry.
- **Fail-closed:** absent → stage parks `Preparing`, unsigned, `NeedsStageCosts` (no provider call); mismatched/stale
  review/expired → refused, nothing recorded; cost increase after acceptance → read-only re-quote before custody
  refuses `NFT_STAGE_COSTS_CHANGED`, a new acceptance supersedes (only while no wire exists).
- **Provider:** `SolanaProvider.NftCosts.cs` quotes stage kinds (shared `InspectNftMovementAsync`, conservative
  token-account rent, worst-case message fee, levy 0); `SolanaProvider.Nft.cs` requires `MatchesNftStageCostReceipt`
  and `Covers` before signing and again at submit; a receipt-less prepared wire still recovers.
- **Intent/consent:** intent factory carries the current stage receipt; `NftStageConsentIdentity.Hash` strips
  `ProtocolCostQuote` (byte-identical for every pre-existing intent) so accepting costs never invalidates stage consent.
- **Visibility:** view adds `NeedsStageCosts`, `StageCostPayerUserId`, `AcceptedStageCostLimits` (owner/participant).

## RV-L5-09 takeover vs stale workflow run

Owner takeover stays allowed at any continuable stage (decision). `AdvanceAsync` refuses a caller carrying
`ActionAuthority` once taken over; its authority-bearing writes pass `workflowAuthority`, and
`SurrealNftLifecycleStore.UpdateAsync` refuses them against the stored row; every lifecycle UPDATE now also predicates on
the unchanged `actor_continuations_json`, so a takeover committed between read and write makes the stale write match no
row. Doc drift in `Services/Users/AGENTS.md` §NFT owner takeover fixed (any continuable state).

## RV-L4-02 (HIGH) recipient resolution

`MoveCoreAsync` no longer scans `GetAllAsync().SingleOrDefault(...)`. `ResolveRecipientWalletAsync`: Platform wallets via
new `INftLifecycleStore.FindCustodialWalletIdsAsync` (typed, `wallet_chain_address` index; Ethereum checksum/lowercase
forms) + the signature-proven wallet from `IWalletOwnershipProofStore.GetAsync`. Unproven rows ignored; two owners →
`NFT_RECIPIENT_AMBIGUOUS`; none → `OPERATION_NOT_ALLOWED`; both before admission. Sweep: no other NFT address→user lookup.

## RV-L1-06 / RV-L1-05

- `CredentialRevokeRefusalTests` already covered unrecorded issuer, non-soulbound, missing holder address, issuer without
  wallet, lookup failure; added third-party caller and holder-of-unrecorded-issuer (the two remaining deleted deny paths).
- `NotEnabledNodeTypeGateTests`: exact set equality restored; new `CredentialRevoke_StaysBlockedUntilItsTrackChecksTheGateRemovalStep`
  ties the gate to the unchecked Phase 4 removal step in `credential-third-party-issuance_20260906/plan.md`.

## Tests (run by filter)

Authored: `Users/NftStageCostTests` (accepted → proceeds with receipt; 5 refusal variants with no provider call;
cost change → re-quote supersedes; credential recipient pays Accept, issuer cannot quote), `Users/NftRecipientResolutionTests`
(custodial and proven owner beat an unproven duplicate; attacker-first refused, never owner; ambiguity typed),
`ActionNftRecoveryTests.AfterOwnerTakeoverTheStaleRunCanNoLongerAdvanceOrWriteTheLifecycle`, the two revoke cases, the gate
test. Harness: `AddWallet` registers custodial (Platform) wallets by default (`external:` for unproven rows); recipient
directory wired from the harness wallets. Integration fixture `SolanaNftProtocolTests` now quotes the Accept stage
(authored, unrun: needs the local validator).

Filter run: `Nft|Solana|Credential|NotEnabledNodeTypeGate|NotEnabledActionNodeTypes|schema goldens` → 638 passed, 0 failed.
Route/cross-tenant gate tests fail only on pre-existing other-lane rows (L1 webhook stores, L2 KYC/hosted rows,
ExternalAuth routes); the fix-fm11 rows are clean. Cut: no provider unit test for stage preparation (the durable fixture
has no NFT asset accounts; a refusal there would pass for the wrong reason).

## Compile result

`dotnet build azoa.sln -c Debug`: 0 errors, 17 warnings (≤ 25). `node scripts/round4/merge-check.mjs`: ok.

## Self-review

- Transaction scope: acceptance = version CAS + payer fence + receipt/expiry fence in one BEGIN/COMMIT; signing gates are
  C# preconditions on the CAS-checked row (consents immutable, part of the row); takeover predicate inside the UPDATE.
- §7 classes: no new raw SurrealQL (typed wallet query, typed UPDATE predicate); null `StageCostConsentsJson` → `Unset`;
  no enum casts; no positional result reads.
- Security: routes `[Authorize]`, captured credential, `financial` rate limit, payer identity + tenant + wallet bound,
  non-payer gets "not found" (no existence oracle), acceptance replay pinned to the same user and tenant; quotation never
  signs; provider re-enforces the receipt.

## Inventories

- `Architecture/Inventories/RouteAuthorization/fix-fm11.json`: the two stage-cost routes (non-value).
- `Architecture/Inventories/CrossTenant/fix-fm11.json`: `INftLifecycleStore.FindCustodialWalletIdsAsync`.

## Deferred (named)

- Real-DB fixtures for the stage-cost acceptance fence (DB-time expiry, race with preparation) and the takeover UPDATE
  predicate: L6 harness; the in-memory store mirrors both.
- SDK methods + browser UI for stage-cost quote/accept (and showing `AcceptedStageCostLimits`): L7 surface. Until then a
  Solana transfer/acceptance parks with `NeedsStageCosts` (safe, unsigned).
- No NFT operator-console view; the accepted limits are in the participant view and the row.
- Workflow-admitted Solana transfers park until the payer accepts with a direct credential under the admitted tenant.

## Needs other lane

- **L7:** SDK `quoteNftStageCosts`/`acceptNftStageCosts` (`POST /api/nft-stage-costs/{id}/quote|accept`, bodies
  `NftStageAcceptance` / `NftStageCostAcceptance`) and the lifecycle UI step; view fields above.
- **r4/fix-chains (Providers/Blockchain):** I edited only `Providers/Blockchain/Solana/SolanaProvider.Nft.cs`,
  `SolanaProvider.NftCosts.cs` and an inserted section in `Providers/Blockchain/Solana/AGENTS.md` (before "Codec provenance").
