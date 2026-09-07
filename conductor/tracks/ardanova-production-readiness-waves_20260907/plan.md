---
type: Track Plan
title: ArdaNova production-readiness waves — plan (chain-first, D44-D51)
description: Eleven dependency-ordered, collision-proofed waves re-sequenced chain-first under owner decisions D44-D51 and revised against RESEQUENCE-CRITIQUE.json; custody posture assumed present, its enabling code path scheduled; ordered by what a non-crypto user hits first.
tags: [plan, ardanova-production-readiness-waves_20260907, pending, chain-first]
timestamp: 2026-09-07T00:00:00Z
resource: ./metadata.json
---

# Plan

Generated from `REVIEW-EVIDENCE.json` (147 findings, 8 verticals, 2 product lenses),
re-sequenced under `conductor/RUNBOOK.md` D44-D48, and revised 2026-09-07 against
`RESEQUENCE-CRITIQUE.json` (25 findings, 2 blockers) and D49-D51. The findings stand;
their order and some of their shape do not. Waves are ordered by the thesis — what a
non-crypto user hits FIRST — within each dependency level. Each wave names its collision
risk; two items in one wave never write the same file.

**Custody posture is ASSUMED PRESENT (D48).** Nothing below gates on KMS/HSM procurement,
key handles, DEK envelopes, float caps or the operator's custody assurance; those live in
the "Operator's own clock" appendix and are not scheduled. **The one custody item that IS
scheduled is W1.8**, because posture being assumed does not make the node's code recognise
it: `Managers/TenantCustodialAccountManager.cs:79-99` computes `custodyAvailable` as
`CustodyMode == "DevelopmentOnly" && IsDevelopment() && Blockchain:Mode == Simulated &&
KEK >= 32`, so in any non-Development environment `walletReady` is false **by
construction** and every wave below inherits the gate. W1.8 teaches the node a production
custody mode. It does not assure it — that is still the operator's, in the appendix.

**Naming (D51).** The deployable composition unit is `ActionGraphBundle` throughout this
plan — a repo-wide rename from its two pre-D51 names is in flight, so expect either of
those to persist in older documents and in source until it lands. Never write
`ActionGraph` unqualified: that name belongs to the `ActionGraph` field on
`DappCompositionRequests`, which is a different concept entirely.

## Why this differs from the 2026-09-07 review plan

| Change | Decision behind it |
|---|---|
| Sponsorship is no longer "Stripe Checkout for a project → ledger row → AZOA anchor later". It is **fiat → ARDA → spend ARDA → receive a real project ASA, on chain**. The old Wave 3 (fiat-first pipes) is dissolved; the chain primitives the review parked in Wave 9 (reserve, IssueUnits, opt-in, HOLD) move to Wave 3 and become the critical path. | **D44** — token-native, chain-first economy; fiat is only the ARDA on/off-ramp. Review decision #8 (ARDA internal-only) rejected: ARDA is the currency users buy, hold and spend. |
| The Stripe rail lives in AZOA as `Fiat.USD`, scoped to the ARDA on-ramp and off-ramp. ArdaNova's `StripeCheckoutGateway`/`StripeWebhookController` migrate to call it; the sidecar and the SDK orchestrator retire; the reversal pipeline (D10) is built once, in the rail. The only contract change for this is line 75 (webhook verification → AZOA) plus the §3 scoping. | **D45** (D43 stands). Review decision #2 rejected. |
| KYC tiering is gone. Every value action — sponsor, worker, owner — needs full document KYC **and** a provisioned custodial wallet. The wallet stays invisible; the identity check does not. Policy (uniformity, AML, Identomat trust profile) is node-level operator config, not per-tenant choice. The "rail-aware, no wallet on fiat" eligibility rework is dropped; the "unavailable vs not-ready" fix stays. | **D46**, **D48** (operator sets compliance policy). |
| A stake is one real per-project ASA minted into a platform reserve; funding and rewards are `IssueUnits` from that reserve. `TokenBalance` stops being a ledger of record and becomes a read-model of chain holdings. `AllocationKind.Mint`-per-reward is retired on both callers. `assetId` is required, never optional. | **D47**. Review decision #5 rejected as the review itself recommended; ledger-first (Q4 option 1) rejected. |
| The custody wave (old W8) and everything that depended on its posture are removed from the schedule and listed in the appendix. The HOLD primitive and the platform reserve wallet stay on the critical path — they are value primitives the node must expose, not custody-assurance questions. Consent grants for child users (old W8.3) and un-parking reconciliation (old W8.4) also stay: they are correctness, not posture. | **D48 (corrected)**: the owner runs the node and owns custody, compliance and completeness; ArdaNova is a tenant. The §2/§11.1 contract amendment is withdrawn. |
| A Wave 0 of **design decisions** replaces the review's seven owner questions (Q1-Q4 are now answered by D44-D47; Q5-Q7 are taken as stated assumptions). D44 opens five new questions the code cannot proceed without: the exchange mechanism, ARDA genesis parameters, project-ASA parameters, who may create a project token, and the off-ramp destination. | **D44** consequences ("exchange undesigned", "ARDA has no genesis"). |
| The first sponsorship is now at the end of **Wave 6**, not Wave 3. **Seven** node subsystems (reserve, genesis, IssueUnits + recipient preparation, HOLD, **per-project treasury accounts**, fiat rail, exchange) must exist first. See §"Scale, honestly". | D44 + D47 together, plus Q-X6. |
| ARDA genesis is **configuration and operations, not a build**. The mint path exists end to end — `ActionNodeType.FungibleTokenCreate` → `Managers/FungibleTokenManager.cs:147` → `IAlgorandASAModule.CreateASAAsync` with a real total and decimals — and `Providers/Blockchain/Algorand/AlgorandProvider.cs:142` has `SupportsFaucet => true`, guarded by the provider contract to dev/test networks. W3.2 shrinks to: decide supply, decimals and the reserve holder; mint on devnet through the faucet; promote the same definition. | **D49**. |
| The ARDA↔project-token exchange is **an Action DAG plus an `ActionGraphBundle`, composed from AZOA's existing primitives — not an AMM**. The old Q-X1 stops being an open decision and becomes design note N-X1. Tinyman leaves ArdaNova's user path entirely. | **D50**. |
| A project's ARDA lives in **a per-project custodial treasury account on the node**, and the exchange group credits it directly. It is not a Postgres balance. | Critique blocker 2; Q-X6. |
| The node is taught to **recognise a production `CustodyMode`** (new W1.8) instead of assuming the recognition already exists. Custody *assurance* stays on the operator's clock; custody *recognition* is a scheduled value primitive, exactly like the reserve wallet. | Critique blocker 1; D48 read precisely. |
| The deployable composition unit is named `ActionGraphBundle` everywhere in this plan, replacing its two pre-D51 names. | **D51**. |

## Dependency graph (chain-first; custody POSTURE assumed, custody RECOGNITION scheduled)

What must exist before a non-crypto user completes the FIRST sponsorship, in dependency
order. Arrows read "needs".

```
first sponsorship (W6.2 settles an intent through the exchange)
 ├─ ArdaNova client: ARDA balance + "Add funds" + holdings without a chain word ...... W6.1, W6.4
 ├─ ArdaNova backend: intent → AZOA exchange op → SETTLED, hold window row .......... W6.2, W6.3
 │    ├─ AZOA exchange (Action DAG + ActionGraphBundle, tenant-declared rate) ....... W5.1
 │    │    ├─ ARDA genesis into the platform reserve (config + ops, D49) ............. W3.2
 │    │    │    └─ platform reserve wallet + fee-payer ................................ W3.1
 │    │    ├─ per-project custodial TREASURY account — the ARDA leg's credit ......... W3.7
 │    │    ├─ IssueUnits + custodial recipient preparation (opt-in, min balance) ...... W3.3
 │    │    └─ HOLD (hold window on freshly issued stake, Q5) .......................... W3.4
 │    └─ one ASA per project minted into the reserve, assetId written ................ W5.2
 │         ├─ fungible-mint wire fix ................................................. W3.5
 │         └─ project publishable + owner KYC-ready ................................... W2.1, W1.x
 ├─ ARDA in the sponsor's custodial wallet: Fiat.USD on-ramp .......................... W4.2
 │    ├─ FiatRailProvider + Stripe termination in AZOA ................................ W4.1
 │    ├─ IssueUnits (ARDA from reserve into the user's wallet) ........................ W3.3
 │    └─ reversal pipeline before real cards ......................................... W4.3
 ├─ uniform KYC + invisible custodial wallet, operator-set policy ..................... W1.1-W1.7
 │    ├─ the node RECOGNISES a production CustodyMode (else walletReady is never true)  W1.8
 │    └─ implied consent grant so tenant-driven signing is allowed .................... W3.6
 └─ Wave 0: decisions, honest docs, a tree that builds ................................ W0.*
```

Parallelism, precisely: Waves 1, 2 and 3 run in parallel **except for the declared
cross-wave waits** — W1.1 and W2.1 wait on W0.5, W2.4 waits on W1.1 and W2.1, and W3.1
waits on W1.4, which owns `oasis-sleek/appsettings.json` and `docs/NODE-HOST.md` through
Wave 1. Within Wave 1, W1.5 and W1.7 wait on W1.8. **Wave 4 follows Wave 3 with no
overlap:** the earlier "W4.1 may start alongside W3" clause is **withdrawn** — W4.1 owns
`Program.cs` and `appsettings.json`, which W3.1 owns during Wave 3 and W1.4 owns during
Wave 1, so three live owners were possible. Wave 5 follows 3 and 4. Wave 6 follows 4 and
5. Waves 7-10 are sequential after 6.

## Wave 0 — The decisions still open, honest documents, and a tree that builds ⬜

*D44-D48 answer Q1-Q4 and take Q5-Q7 as assumptions. Of the five design questions D44
opened, **D49 and D50 have since answered two** — ARDA genesis and the exchange mechanism —
which are recorded below as design notes, not decisions. **Q-X6 is new** (who holds a
project's ARDA) and is ANSWERED in this revision, because the plan cannot be
collision-proofed without an answer; the owner may reverse it before W3.7 lands. That
leaves **four decisions still open** that Wave 3 cannot start without. Documents still
describe the wrong model (self-sovereign, sidecar fiat, fiat-first tokenomics, tracks
marked complete that are 501) — including this track's own `spec.md` and `metadata.json`.
The ~81-file in-flight hardening set must land or every ArdaNova item below edits an
untracked file.*

### Design notes — closed by D49/D50 and by this revision, kept so the reasoning survives

- **N-X1 — the exchange is an Action DAG plus an `ActionGraphBundle`, composed from AZOA's
  existing primitives. NOT an AMM.** (**D50**; replaces the old Q-X1, which is no longer a
  question.) The `ActionGraphBundle` is the deployable unit — it carries a target chain,
  generated code, a deployment config and bound holon ids, with `/generate` and `/deploy`
  already routed — and the Action DAG supplies the orchestration over value nodes that
  already exist in some form (`Grant`, `Transfer`, `FungibleTokenCreate`). Three
  consequences the code must hold onto:
  1. **The existing `Swap` node is NOT this exchange.** It delegates to DEX adapters.
     Nothing is wired through `ISwapManager` by default, and wiring it would be a separate,
     deliberate decision. `SwapManager` stays DEX-only and off ArdaNova's path.
  2. **Rate policy stays ArdaNova's** under D7/D16 — AZOA moves already-decided amounts. A
     treasury-rate exchange therefore means **ArdaNova computes the rate and the bundle
     executes the move**, with rate + source + version stamped on the op (W4.6). The review
     already found `ExchangeService` is treasury-rate, so this is the shape the code is in.
  3. **A bundle runs under the operator's node**, with a private-key hash and a deploy
     route, so it **inherits the HOLD requirement (D14 / W3.4) rather than escaping it**.
     Value moved by a bundle is value moved by the node, under the node's custody posture.
  This also **retires Tinyman from ArdaNova's user path**, which the review wanted anyway —
  it found the AMM off the user path and carrying a decimals defect on a money path. The
  AMM is rejected, not deferred; a second `IExchangeVenue` stays addable later without
  touching ArdaNova.
- **N-X2 — ARDA genesis is configuration and operations, not a build.** (**D49**; replaces
  most of the old Q-X2.) Verified against source: `ActionNodeType.FungibleTokenCreate` →
  `Managers/FungibleTokenManager.cs:147` → `IAlgorandASAModule.CreateASAAsync` already
  mints with a real total and decimals, and `Providers/Blockchain/Algorand/AlgorandProvider.cs:142`
  has `SupportsFaucet => true`, guarded by the provider contract to dev/test networks —
  exactly the intended test path. So W3.2 is: **decide supply, decimals and the reserve
  holder; mint on devnet through the faucet; promote the same definition to mainnet.**
  Parameters taken as configuration unless the owner says otherwise: one large fixed total
  (e.g. 10^15 base units) at 2 decimals, minted once into the platform reserve wallet — an
  operator-owned custodial account on the node (D48); "circulating supply" is reserve
  outflow; on-ramp issues from the reserve, off-ramp returns to it; manager / reserve /
  freeze / clawback roles all = the reserve. *Spendability:* on-ramped ARDA is spendable
  immediately (a 120-day freeze would kill sponsorship); the D10 compensation saga (W4.3)
  covers the chargeback tail. **Reversal cost: changing supply or decimals after genesis
  means a second ASA — decide once, prove it on devnet, then promote.**
- **N-X3 — a project's ARDA is held in a per-project custodial treasury account on the
  node.** (Answers Q-X6, below.) Recorded here because W3.7, W5.1, W5.2, W5.3, W6.2, W8.2,
  W8.3 and W10.1 all read it.

### Decisions still open before Wave 3 (options, recommendation, reversal cost)

- [ ] **Q-X2 — the ARDA price per US cent.** *(All that survives of the old Q-X2 — supply,
  decimals and the reserve holder moved to N-X2 under D49. This is the last unanswered
  piece of the on-ramp.)* How many ARDA does one US cent buy, and **whose parameter is
  it**? The retired sidecar treated its `TOKENS_PER_CENT` schedule as an operator-configured
  **rail parameter**; D7/D16 say AZOA never computes economics, which makes the price
  **consumer economics** that ArdaNova registers. Under D48 the operator sets compliance
  policy on the node, which makes an operator-set price defensible — but it is the owner's
  call. *Recommendation:* a fixed **1 ARDA = $1.00** peg for v1 ("1 ARDA is one dollar" is
  the only sentence a schoolteacher needs), registered by ArdaNova as a **versioned price
  schedule** with the node (consumer economics), and **applied** by the rail at webhook time
  from Stripe-confirmed `amount_total` × that registered schedule, never from client
  metadata (the sidecar §pricing invariant, ported). *Off-ramp consequence:* only ARDA past
  the reversal window of its on-ramp **lot** may be cashed out — the source-aged rule, with
  the lot ledger written by W4.2 and consumed FIFO by W8.1. Reversal cost: the schedule is
  versioned and additive, so the *price* is cheap to change; **who owns the price** is a
  contract-shaped decision and is not.
- [ ] **Q-X3 — Project-ASA parameters.**
  *Supply/decimals:* owner-declared `totalSupply` from `ProjectTokenConfig`, decimals 0
  (share-like, integer units) unless the owner opts into fractional. *Reserve:* the platform
  reserve wallet holds the whole supply; `contributorSupply` / `investorSupply` are ledger
  partitions of that one reserve, not separate wallets. *Roles:* manager, reserve, freeze
  and clawback addresses = platform reserve. *Clawback per Q5:* Algorand's clawback address
  can be set to zero later but never re-enabled, so "clawback only while the hold window is
  open" cannot be expressed per asset — the hold window is implemented by the **HOLD ledger**
  (units stay in the holding wallet until release, W3.4), and clawback stays on the asset as
  the disclosed exceptional fraud/dispute path. *Recommendation:* as stated; disclose in
  user terms ("rewards become yours after N days; the platform may reverse fraudulent
  payments"). Reversal cost: per-asset config at mint; existing assets keep theirs.
- [ ] **Q-X4 — Who may create a project token.**
  Options: (a) the project owner, self-serve at publish, via ArdaNova's tenant credential
  calling AZOA fungible-token create into the reserve under the existing
  `NodeGovernanceGuard`; (b) operator approval per project; (c) ArdaNova admin only.
  *Recommendation:* (a), with the owner required to be KYC-ready (D46 — they will receive
  project tokens) and the node governance guard as the operator's brake. Reversal cost: low.
- [ ] **Q-X5 — ARDA → fiat off-ramp destination.**
  Options: Stripe Connect Express payouts (per-user connected account; ArdaNova already has
  `CreateConnectedAccountAsync`/`CreatePayoutTransferAsync` to port) vs Stripe Treasury vs
  manual bank transfer. *Recommendation:* Connect Express inside the AZOA rail, onboarding
  at first cash-out (the one moment paperwork is expected). Reversal cost: medium — the
  rail's off-ramp saga is venue-agnostic; the Connect gateway is one class. ⚠ Stripe
  Connect **platform approval** is an external clock with no bound this plan can set; it
  should be started at Wave 0, not Wave 8.
- [x] **Q-X6 — who holds a project's ARDA. ANSWERED in this revision; the owner may reverse
  it before W3.7 lands.** Options: (a) **a per-project custodial treasury account on the
  node** — the exchange group moves ARDA user→**project treasury** while the project ASA
  moves reserve→user, so a project's funds are chain truth under the operator's custody;
  (b) the platform reserve holds every project's ARDA and ArdaNova's Postgres records whose
  it is; (c) the project owner's own custodial wallet. **Answer: (a).** (b) is exactly the
  ledger-of-record-for-real-value that **D47 rejected on the stake side**, surviving on the
  project side: `TreasuryService`'s bucket balances (`indexFund`/`liquidReserve`/`operations`)
  plus `PlatformTreasuryTransaction` rows would be the only record of whose ARDA the reserve
  holds, and D44's *"spend ARDA to FUND A PROJECT"* would fund nothing on chain. (c) puts a
  project's operating funds behind one person's KYC, offboarding and disputes. Reversal
  cost: **medium** — one node service (W3.7) and the destination leg of the exchange group;
  reversing after projects hold balances means an on-chain sweep, not a config change.

*Assumed from the review, reversible at their wave:* **Q5** hold window on stake +
exceptional clawback, disclosed (W3.4, W5.2). **Q6** ArdaNova is the vote of record, one
member one vote, AZOA governance stays gated (W9). **Q7** posts stack kept as project-scoped
field notes, hardened (W2.5).

### Items

- [ ] **W0.1** (both, S, risk low) — Amend the integration contract (both copies, byte-identical): line 75 webhook signature verification → AZOA and §3 "AZOA owns the `Fiat.USD` rail, scoped to ARDA on-ramp/off-ramp; ArdaNova owns the funding intent and the rate" (D45); §11.2 tenant-custodial not self-sovereign (ratified decision #1); §1 KYC document capture = AZOA-hosted Identomat, policy node-level (D46/D48); §4.1 credential path disabled; §5.1 Project holon = target-state; §7 P7 done. **In §2, correct the ONE phrase** that calls users "first-class self-sovereign identities" (contract line 95) to "tenant-custodial child users; the operator is the custodian of record" — after the §11.2 amendment the contract otherwise contradicts itself. **Leave §2's operator/consumer split and §11.1 ("ArdaNova owns no KMS") untouched** — D48 protects those, not that phrase.
      *thesis:* Stops the next engineer rebuilding the self-sovereign path or a second Stripe path
      *owns:* `oasis-sleek/conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`, `ardanova/conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`
- [ ] **W0.2** (ardanova, M, risk low) — ArdaNova doc sync to D44-D47: product.md (ARDA is the user-facing currency; KYC uniform and gates value not creation; stake = per-project ASA; governance off-chain; KYB stance), 09-tokenomics plan/spec rewritten to the fiat→ARDA→project-token→ARDA→fiat loop (drop USDCa, drop "ARDA internal"), treasury-reward plan (IssueUnits from reserve, never Mint), 15-token-equity-ui plan (UI exists), 20-dispute-escrow-ui spec (HOLD-backed), tracks.md (Phase E header, 08-kyc superseded, azoa-user-onboarding overview, AZOA Provider Adapter row), README (Voting → 501, escrow row), ARCHITECTURE.md (retire Explorer), RUNBOOK §6 governance
      *thesis:* Planning off these docs double-builds UI that exists and assumes a stablecoin rail that does not
      *depends on:* W0.5
      *owns:* `ardanova/conductor/**` (except the contract file), `ardanova/README.md`, `ardanova/documentation/ARCHITECTURE.md`, `ardanova/documentation/FRONTEND_EXPERIENCE.md`
- [ ] **W0.3** (azoa, M, risk low) — AZOA doc sync: RUNBOOK (D9 sidecar bullet marked superseded by D43/D45; D10 status; D36 "sign-without-export" wording corrected to "does not exist yet — operator's clock"; D44-D51 cross-links), conductor/product.md rewritten to the RUNBOOK §1 goal + two-consumer table + the D44 loop, README.md:108/122, API_SYNC.md credential issue route, tracks.md (credential issuance landed, governance D39/D40/D42 landed), governance-vote-of-record and credential-third-party-issuance track docs, docs/TENANT-CUSTODIAL-ONBOARDING.md, stale `IKeyCustodyService`/`KeyCustodyService` FOLLOW-UP comments. **Also this track's own `spec.md` and `metadata.json`**, which described the REJECTED fiat-first model ("the honest path is fiat-first … put stake in ArdaNova's own ledger … let AZOA's chain be the anchor"), "10 waves, 46 items", AC1 "sponsor a project with a card" (D44: a card buys ARDA; it never sponsors a project), `owner_questions: 7` and partitions pinned to `2cd6616` — **that rewrite LANDED with this plan revision on 2026-09-07**; W0.3 keeps ownership so no other item opens those two files. The D27-vs-D36 reconciliation is NOT done here — it is the operator's clock.
      *thesis:* Reviews cannot check thesis alignment against a product.md that does not state the thesis, or against a spec.md that still argues for the model the owner rejected
      *owns:* `oasis-sleek/conductor/RUNBOOK.md`, `oasis-sleek/conductor/product.md`, `oasis-sleek/conductor/tracks.md`, `oasis-sleek/conductor/tracks/ardanova-production-readiness-waves_20260907/spec.md`, `oasis-sleek/conductor/tracks/ardanova-production-readiness-waves_20260907/metadata.json`, `oasis-sleek/conductor/tracks/governance-vote-of-record_20260906/**`, `oasis-sleek/conductor/tracks/credential-third-party-issuance_20260906/**`, `oasis-sleek/README.md`, `oasis-sleek/API_SYNC.md`, `oasis-sleek/docs/TENANT-CUSTODIAL-ONBOARDING.md`, `oasis-sleek/Services/Custody/IKeyCustodyService.cs (comments only)`, `oasis-sleek/Services/Custody/KeyCustodyService.cs (comments only)`
- [ ] **W0.4** (both, S, risk low) — Publish azoa-sdk 0.3.0 (or drop ArdaNova's ^0.3.0 pin) so ArdaNova CI is not red by construction
      *thesis:* None directly; unblocks every ArdaNova client PR
      *owns:* `oasis-sleek/sdk/azoa-wallet/package.json`, `ardanova/ardanova-client/package.json`, `ardanova/ardanova-client/package-lock.json`
- [ ] **W0.5** (ardanova, M, risk medium) — Land the in-flight ~81-file hardening set (AzoaValueEligibilityService, FundingIntentService, AzoaAllocationService, invest page, escrow 501/503s) on a branch that builds; then delete the dead self-sovereign path (AzoaAvatarService, IAzoaAvatarNode, AzoaAvatarNodeAdapter, AzoaPublicNodeClient, AzoaAvatarController 410 stub, DI registration)
      *thesis:* Removes the one code path that would post a user's real email to a shared node and fail on 'username taken'
      *owns:* `ardanova/ardanova-backend-api-mcp/api-server/src/** (in-flight set as reported by git status)`, `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaAvatarService.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaPublicNodeClient.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaAvatarNodeAdapter.cs`, `ardanova/.../ArdaNova.API/Controllers/AzoaAvatarController.cs`, `ardanova/.../ArdaNova.Application/DependencyInjection.cs`

⚠ **Collision:** W0.2 and W0.5 both touch ardanova: W0.5 lands code first, W0.2 edits only conductor/docs afterward. W0.1 owns the contract file in BOTH repos; W0.2/W0.3 must not touch it. W0.3 owns API_SYNC.md in this wave (W4.7 owns it later) and is the sole owner of this track's `spec.md`/`metadata.json` (W0.2 owns `ardanova/conductor/**`, a different repo).

## Wave 1 — Account setup that works: uniform KYC + an invisible custodial wallet, operator-set policy ⬜

*The first thing a non-crypto user hits after Google sign-in is 'Start account setup' → 409 'unavailable' (EnableCustodialAccounts=false) and, if enabled, an Identity check 'not configured on this node' (Kyc:Provider=unavailable, empty trust profile). Under D46 every value action needs identity + KYC + a provisioned wallet, so this wave makes all three real in production and strips the genuine crypto leaks (address on the account card, 'Wallets' nav, the external-wallet route). Custody POSTURE is assumed; custody RECOGNITION is not — W1.8 is the item that lets `walletReady` be true outside Development at all, and without it every downstream wave is silently blocked on the thing this plan claims to assume. Parallel with Waves 2 and 3.*

- [ ] **W1.1** (both, M, risk medium) — Eligibility = identity + KYC-approved + walletReady, uniformly (D46); distinguish 'service unavailable' from 'not ready' instead of swallowing exceptions as Forbidden; remove the `avatarId.HasValue &&` fail-open shape; mirror in the client contract's `isValueActionAccountReady`; add the checklist note that per-person limits are expressed as AZOA uniqueness requirements, never DB counts
      *thesis:* An AZOA outage stops reading as 'you did not finish setup'; the gate says the one true thing
      *depends on:* W0.5
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaValueEligibilityService.cs`, `ardanova/.../ArdaNova.Application/Services/Interfaces/IAzoaValueEligibilityService.cs`, `ardanova/.../tests for eligibility`, `ardanova/ardanova-client/src/lib/contracts/project-create-contract.ts`
- [ ] **W1.2** (ardanova, S, risk low) — Strip crypto from settings and account surfaces: account card shows 'Ready'/'Not created' (no address, no GUID, no 'Azoa'); **delete the `/settings/wallets` route entirely** (public-address entry, `shortenAddress`, signed challenge), delete its sidebar item, and **delete the payouts page's 'MANAGE EXTERNAL WALLETS' CTA** — ratified decision #9 says no external wallets and D46 says the wallet stays invisible, so an 'Advanced' opt-in is a rejected premise kept alive one click from a non-crypto user; verification page drops 'wallet custody' copy, adds status re-poll on window focus after the Identomat tab; update components/azoa/AGENTS.md. A 'claim to an external wallet' feature, if ever wanted, is a future designed decision — not a surviving toggle.
      *thesis:* Direct — the only places a sponsor is asked to think about wallets, addresses, mnemonics, or signed proofs
      *owns:* `ardanova/ardanova-client/src/components/azoa/custodial-account-card.tsx`, `ardanova/ardanova-client/src/components/azoa/AGENTS.md`, `ardanova/ardanova-client/src/components/app-sidebar.tsx`, `ardanova/ardanova-client/src/app/settings/wallets/page.tsx (delete, with its route references)`, `ardanova/ardanova-client/src/app/settings/verification/page.tsx`, `ardanova/ardanova-client/src/app/settings/payouts/page.tsx`
- [ ] **W1.3** (ardanova, S, risk low) — First-run onboarding: soft 'Finish account setup' prompt on the dashboard after first sign-in so the identity check happens before intent, not at checkout (Track 17 minimum); copy states plainly that an identity check is required before funding or being paid (D46 is policy, say so)
      *thesis:* Friction moves out of the moment of generosity
      *owns:* `ardanova/ardanova-client/src/app/dashboard/page.tsx`, `ardanova/ardanova-client/src/components/onboarding/setup-prompt.tsx (new)`
- [ ] **W1.4** (azoa, M, risk medium) — AZOA KYC production readiness with node-level policy (D48): confirm where approval ExpiresAt is stamped and split Kyc:ApprovalValidityDays (12 months) from SubmissionExpiryDays; Identomat trust profile, PolicyVersion, AssuranceLevel, TrustedProviderKeys become node config with Identomat as the node default provider; **the per-tenant `PUT /api/tenant/kyc/provider` override defaults OFF and only the operator may enable it, per tenant, through a node-config allow-list** — the KYC provider *is* the compliance policy, and a tenant-selectable override enabled by default is tenant-chosen policy, which is the wrong-custodian premise in a smaller form (D48); check Identomat /begin for a return/redirect parameter and send it; **close the public self-register hole: `Registration:Enabled=false` in Production gates `POST /api/user/register` (`Controllers/UserController.cs:24-31`, `[AllowAnonymous]`, no config gate today) and a unique index on email/username replaces `UserManager.RegisterAsync`'s racy O(n) uniqueness scan** — the same node custodies ArdaNova's real users, so gotcha 22 cannot be accepted by the name "no public self-register on the node" until this lands; operator runbook steps in NODE-HOST.md, including the production `CustodyMode` value W1.8 teaches the node to recognise
      *thesis:* Without it 'Identity check' is 'not configured'; a 30-day approval would force monthly re-verification of every sponsor; and a public, racy register endpoint sits on the node that holds every ArdaNova user
      *owns:* `oasis-sleek/Managers/KycManager.cs`, `oasis-sleek/Managers/KycControlPlaneManager.cs`, `oasis-sleek/Providers/Kyc/IdentomatKycProviderService.cs`, `oasis-sleek/Controllers/TenantKycConfigurationController.cs`, `oasis-sleek/Controllers/UserController.cs`, `oasis-sleek/Managers/UserManager.cs`, `oasis-sleek/appsettings.json (Kyc + Registration sections)`, `oasis-sleek/docs/NODE-HOST.md (KYC + custody-mode + registration operator sections)`, `oasis-sleek/tests/.../Kyc/* (except *Outcome*.cs and *Screening*.cs, which are W1.5's)`
- [ ] **W1.5** (azoa, M, risk medium) — AML non-clearance recourse: UnderReview status on the tenant custodial-account projection (no AML reason disclosed), manual-review SLA, operator KYC console lists the review queue with approve/deny. While in `TenantCustodialAccountManager`, **replace the inline `custodyAvailable` expression (lines 79-99) with a call to W1.8's `CustodyModeResolver`** — one line, so the production custody mode reaches the capability projection and `unavailableReason` stays honest.
      *thesis:* A false-positive sanctions-name match strands a legitimate sponsor forever with no path
      *depends on:* W1.8
      *owns:* `oasis-sleek/Services/Kyc/KycHostedSessionOutcomeHandler.cs`, `oasis-sleek/Services/Kyc/KycScreeningRiskPolicy.cs`, `oasis-sleek/Managers/TenantCustodialAccountManager.cs`, `oasis-sleek/Models/Responses/TenantCustodialAccount*.cs`, `oasis-sleek/frontend/src/app/(dashboard)/kyc/page.tsx`, `oasis-sleek/tests/.../Kyc/*Outcome*.cs`, `oasis-sleek/tests/.../Kyc/*Screening*.cs`
- [ ] **W1.6** (ardanova, S, risk low) — ArdaNova KYC projection: consume UnderReview ('Under review — contact support'), remove the legacy KycService PRO writer so AZOA is the single writer of verificationLevel, audited operator-only 'unbind AZOA account' action for node-rebuild recovery of the unique azoaAvatarId
      *thesis:* Two writers to verificationLevel disagree; a dev volume wipe Conflict-locks every user until the column is nulled by hand
      *depends on:* W1.5
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaCustodialAccountService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/KycService.cs`, `ardanova/.../ArdaNova.API/Controllers/AdminAzoaBindingController.cs (new)`, `ardanova/ardanova-client/src/app/admin/kyc/page.tsx`
- [ ] **W1.7** (ardanova, S, risk low) — Production config: Azoa:EnableCustodialAccounts=true with CustodyApiKey; identity, KYC AND wallet provisioning all proceed; the node-side `CustodialAccounts:CustodyMode` production value and the KEK secret are set as Railway variables on the AZOA service (the value is the operator's to choose — **W1.8 is what makes the node recognise it**, so this item depends on W1.8, not on the operator's clock); Identomat secrets on the AZOA Railway service; startup validation names the missing leg in the status `unavailableReason`
      *thesis:* Turns 'Start account setup' from a 409 into a working identity + wallet flow
      *depends on:* W1.4, W1.8
      *owns:* `ardanova/.../ArdaNova.API/appsettings.json`, `ardanova/.env.example`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaSettings.cs`, `Railway variables (both services)`
- [ ] **W1.8** (azoa, M, risk medium) — **The node recognises a production custody mode.** Today `Managers/TenantCustodialAccountManager.cs:79-99` sets `custodyAvailable` only when `CustodyMode == "DevelopmentOnly" && IsDevelopment() && Blockchain:Mode == Simulated && KEK.Length >= 32`, so outside Development `walletReady` is false by construction and W1.7's "identity, KYC AND wallet provisioning all proceed", W1.1's uniform gate, W4.2's issuance to a wallet, W5.1 and every W6.x downstream are unfulfillable. Extract the predicate into a pure `Services/Custody/CustodyModeResolver.cs` (over `IConfiguration` + `IHostEnvironment`, no DI registration and therefore no `Program.cs` edit — the KEK is already boot-validated at `Program.cs:142`) that **also recognises a non-development mode (`Platform`/`Node`) when the KEK secret is present and `Blockchain:Mode` is not Simulated**; gate `WalletManager.GenerateWalletAsync` on it so every wallet-creating path — `AllocationManager.EnsureWalletAsync` and `FungibleTokenManager.EnsureWalletAsync` both call it — inherits one answer instead of three; `unavailableReason` stays honest per mode (unrecognised mode, missing KEK, Simulated-in-Production). **This is the enabling half only.** Custody *assurance* — float caps, the Production boot guard, `RealValueReadinessHealthCheck` reading all three legs, `CustodyMode=Disabled` actually disabling — stays on the operator's clock in the appendix and consumes this same resolver rather than re-deriving it.
      *thesis:* Invisible; without it 'Start account setup' can never succeed anywhere but a developer's laptop, and the plan's central assumption is unfulfillable
      *owns:* `oasis-sleek/Services/Custody/CustodyModeResolver.cs (new)`, `oasis-sleek/Managers/WalletManager.cs`, `oasis-sleek/tests/AZOA.WebAPI.Tests/Custody/CustodyModeResolverTests.cs (new)`

⚠ **Collision:** W1.2 and W1.3 both edit ardanova-client: W1.2 owns settings/* + the card + sidebar; W1.3 owns dashboard/page.tsx and the new prompt only. Three items now touch AZOA custody/KYC and each owns disjoint files: **W1.4** owns KycManager, the control plane, Identomat, the tenant-config controller, `UserController.cs`/`UserManager.cs`, `appsettings.json` and `NODE-HOST.md`, plus `tests/.../Kyc/*` **except** `*Outcome*.cs`/`*Screening*.cs`; **W1.5** owns the outcome handler, the risk policy, `TenantCustodialAccountManager.cs`, the `TenantCustodialAccount*` responses, the operator KYC page and those two test globs; **W1.8** owns `Services/Custody/CustodyModeResolver.cs` (new), `Managers/WalletManager.cs` and its own new test file, and touches **no** file W1.4 or W1.5 owns — the one line in `TenantCustodialAccountManager` that adopts the resolver is written by W1.5, which depends on W1.8. W1.6 depends on W1.5's new status value; W1.7 depends on W1.4 and W1.8. **Cross-wave:** W1.4 owns `oasis-sleek/appsettings.json` and `docs/NODE-HOST.md` — W3.1 (parallel) must not open either until W1.4 lands. `Managers/WalletManager.cs` is owned by no Wave 2 or Wave 3 item.

## Wave 2 — Present a problem properly: edit, publish, media, discovery, community surface ⬜

*The social core is half-built for every user: a creator cannot fix a typo, every DRAFT is public, no image reaches the record, search misses the problem statement, discovery is a client filter over 50 rows, and the only community REST surface is unauthenticated with caller-supplied identities. No AZOA involvement. Parallel with Waves 1 and 3. Unchanged from the review plan; Q7 assumed option 2.*

- [ ] **W2.1** (ardanova, M, risk medium) — Backend: public listing defaults to status != DRAFT unless viewer is creator/member; PublishAsync reachable and idempotent; SearchAsync indexes problemStatement/solution/targetAudience; server-side pagination honoured; recovery drafts private; **lands the `ProjectRepository` mapping for the summary / media-paths / funding-goal columns W2.4 adds to the `Project` entity** (W2.4 depends on this item precisely so the repository stays single-owner)
      *thesis:* A person drafting a sensitive problem is exposed before they chose to present it
      *depends on:* W0.5
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Repositories/ProjectRepository.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectServices.cs`, `ardanova/.../ArdaNova.API/Controllers/ProjectsController.cs (list/search/publish actions only)`
- [ ] **W2.2** (ardanova, M, risk low) — Client: pass search/category/type/status/page to getAll and paginate server-side; Publish button + Edit link in the detail header for managers; stop rendering description twice
      *thesis:* The 51st problem becomes discoverable; creators choose when they go public
      *depends on:* W2.1
      *owns:* `ardanova/ardanova-client/src/app/projects/page.tsx`, `ardanova/ardanova-client/src/app/projects/[slug]/page.tsx`, `ardanova/ardanova-client/src/server/api/routers/project.ts`
- [ ] **W2.3** (ardanova, M, risk low) — Edit route /projects/[slug]/edit reusing the wizard's step-0 fields against api.project.update; delete the orphaned project-form.tsx
      *thesis:* The home page's 'Iterate — revise or stop' step becomes real
      *owns:* `ardanova/ardanova-client/src/app/projects/[slug]/edit/page.tsx (new)`, `ardanova/ardanova-client/src/components/projects/project-edit-form.tsx (new)`, `ardanova/ardanova-client/src/components/project-form.tsx (delete)`
- [ ] **W2.4** (ardanova, L, risk medium) — Media and the ask: wire attachment.getUploadUrls into the wizard, persist bucket paths as a JSON array, add a one-line summary field and an optional funding goal (denominated in ARDA, with a USD reference), move the account-status panel to the Team Roles step; render a media strip in OverviewTab; stop printing raw 'Images: {string}' in updates-tab. **The new fields land on the `Project` entity with their own Wave-2 migration** — a DTO-only change would leave them dead or force an edit into W2.1's repository file.
      *thesis:* 'As much clarity and context as possible' is five plain textareas while storage sits finished
      *depends on:* W1.1, W2.1
      *owns:* `ardanova/ardanova-client/src/app/projects/create/page.tsx`, `ardanova/ardanova-client/src/lib/contracts/project-create-contract.ts`, `ardanova/ardanova-client/src/components/projects/overview-tab.tsx`, `ardanova/ardanova-client/src/components/opportunities/updates-tab.tsx`, `ardanova/ardanova-client/src/server/api/routers/attachment.ts`, `ardanova/.../ArdaNova.Application/DTOs/ProjectDtos.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/Project.cs`, `ardanova/.../ArdaNova.Infrastructure/Migrations/*_ProjectPresentationFields (new)`
- [ ] **W2.5** (ardanova, L, risk high) — Posts stack per Q7 option 2: harden (ActorAssertion policy, actor-bound authorId/userId, visibility predicate in GetFeedAsync) and build the project-scoped 'field notes' stream; no global feed
      *thesis:* Anyone can post as anyone and read members-only posts on the live Railway API today
      *owns:* `ardanova/.../ArdaNova.API/Controllers/PostsController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/PostService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/Post*.cs`, `ardanova/ardanova-client/src/lib/api/ardanova/endpoints/posts.ts`, `ardanova/ardanova-client/src/server/api/routers/post.ts`, `ardanova/ardanova-client/src/components/feed/** (new)`

⚠ **Collision:** ProjectServices.cs and ProjectRepository.cs belong to W2.1 only; W2.4's backend edits are confined to ProjectDtos.cs, the `Project` entity and its own migration, and it depends on W2.1 so the repository mapping for those columns is written once, by W2.1. [slug]/page.tsx belongs to W2.2; W2.3 only creates the edit route. create/page.tsx belongs to W2.4 (W5.2 owns its funding step later). Migrations: W2.4 is the only Wave-2 item with one. W1.1 (parallel wave) owns project-create-contract.ts in Wave 1 — W2.4 waits for W1.1 before opening it (already a declared dependency).

## Wave 3 — The node's value primitives: reserve wallet, ARDA genesis, project treasuries, project ASAs into the reserve, IssueUnits, HOLD, consent ⬜

*Under D44/D47 nothing of value moves without these, and none exists: there is no platform reserve wallet, ARDA has no genesis (supply always 0), no project holds anything on chain, `AllocationKind` is Mint|Transfer only (Mint creates a fresh ASA per allocation), a fresh custodial wallet cannot receive an ASA (`OptInAsync` has zero callers, no min-balance funding), nothing anywhere can HOLD (D14), and tenant-driven signing for child users has no consent grant. AZOA-only; multi-session; parallel with Waves 1-2. The review parked this in Wave 9 behind custody; with custody posture assumed (and its recognition scheduled as W1.8) it is the first engineering wave. Seven items — the seventh, W3.7, is the project treasury Q-X6 answers.*

- [ ] **W3.1** (azoa, L, risk high) — Platform reserve wallet: an operator-owned custodial account on the node that holds ARDA and every project ASA, plus the fee-payer role; `IPlatformReserve` (address, sign-as-reserve via KeyCustodyService, balance per asset); `FungibleTokenManager.CreateAsync` gains a mint-into-reserve target with manager/reserve/freeze/clawback = reserve (Q-X3); **reserve ledger row per asset carrying NAMED PARTITIONS** (issued, held, returned, per partition — `contributor`, `investor`, and any the tenant declares) so oversubscription across partitions is checkable on the node, not only in ArdaNova's DB; Reserve config section; NODE-HOST.md reserve section. Owns Program.cs for the wave and adds the one-line registration calls for W3.3/W3.4/W3.6/W3.7's extension files.
      *thesis:* Invisible; without it there is no 'from' for any unit a sponsor or worker ever receives
      *owns:* `oasis-sleek/Services/Reserve/IPlatformReserve.cs (new)`, `oasis-sleek/Services/Reserve/PlatformReserveService.cs (new)`, `oasis-sleek/Managers/FungibleTokenManager.cs`, `oasis-sleek/Models/Requests/FungibleMintRequest.cs`, `oasis-sleek/Persistence/SurrealDb/Models/PlatformReserveLedger.cs (new) + generated schema`, `oasis-sleek/appsettings.json (Reserve section)`, `oasis-sleek/Program.cs`, `oasis-sleek/docs/NODE-HOST.md (reserve section)`, `oasis-sleek/tests/.../Reserve/PlatformReserveTests.cs (new)`
- [ ] **W3.2** (azoa, S, risk medium) — **ARDA genesis: configuration and operations over the mint path that already exists (D49), not a chain integration.** `ActionNodeType.FungibleTokenCreate` → `Managers/FungibleTokenManager.cs:147` → `IAlgorandASAModule.CreateASAAsync` already mints with a real total and decimals, and `AlgorandProvider.cs:142` has `SupportsFaucet => true`, guarded to dev/test networks. So the build here is thin: a one-time, **idempotent operator command** that drives that existing path into the reserve with the N-X2 parameters (total, decimals, unit name, roles = reserve), **persists the asset id in a `platform_token` node-state record** — `appsettings.json` is W3.1's file and is not writable at runtime — and refuses a second genesis; `circulatingSupply` = reserve outflow, read from the reserve ledger. The rest is operations: **mint on devnet through the faucet, verify the definition, then promote the identical definition to mainnet.** Runbook doc carries the promotion checklist and the "decide once" warning (a changed supply or decimals means a second ASA).
      *thesis:* The first thing on the critical path: every ARDA balance, price and cash-out reads 0 until this runs
      *depends on:* W3.1
      *owns:* `oasis-sleek/Services/Reserve/PlatformTokenGenesis.cs (new)`, `oasis-sleek/Controllers/Admin/PlatformTokenController.cs (new)`, `oasis-sleek/Models/Requests/PlatformTokenGenesisRequest.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/PlatformToken.cs (new) + generated schema`, `oasis-sleek/docs/PLATFORM-TOKEN-GENESIS.md (new)`, `oasis-sleek/tests/.../Reserve/PlatformTokenGenesisTests.cs (new)`
- [ ] **W3.3** (azoa, XL, risk high) — `AllocationKind.IssueUnits`: transfer from the reserve, idempotent through the existing ledger, with caller-supplied Purpose landing in operation_log; custodial recipient preparation (fund min balance from the fee-payer, sign the recipient opt-in via KeyCustodyService, then transfer — one atomic group); fee quote exposed before terms lock; provider errors mapped to consumer-safe codes before they cross the tenant boundary; Transfer fee > 0 no longer hard-denies; retire Mint as a caller-selectable kind for tenants
      *thesis:* The very first real issuance to a real user otherwise fails with a blockchain error naming an ASA and 'opt-in'
      *depends on:* W3.1
      *owns:* `oasis-sleek/Managers/AllocationManager.cs`, `oasis-sleek/Models/Requests/AllocationRequest.cs`, `oasis-sleek/Services/Custody/CustodialRecipientPreparer.cs (new)`, `oasis-sleek/Services/Custody/CustodialRecipientPreparerRegistration.cs (new)`, `oasis-sleek/Providers/Blockchain/Algorand/AlgorandProvider.cs`, `oasis-sleek/Managers/NodeFeeScheduleManager.cs`, `oasis-sleek/Persistence/SurrealDb/Models/OperationLog.cs`, `oasis-sleek/Core/ConsumerSafeErrors.cs (new)`, `oasis-sleek/tests/.../Allocation/**`
- [ ] **W3.4** (azoa, XL, risk high) — HOLD primitive (D14): Hold / Release / Refund **/ ReleaseIntoHold(`releaseNotBefore`)** Tier-2 node quartet plus a REST surface, on a platform-custodial holding wallet behind KeyCustodyService, with a hold ledger row (holdId, funder, beneficiary, asset, amount, state, releaseNotBefore) as the idempotency anchor; the hold window (Q5) is expressed as `releaseNotBefore` so freshly issued stake sits in the holding wallet until the window elapses; **`ReleaseIntoHold` exists because W7.2's "approve completion" is a release into a SECOND hold (the worker's window), which a plain Release cannot express**; a hosted sweep releases due holds; TEAL escrow later. **Every outbound move — release, refund, and the holding wallet's own opt-in to each project ASA — goes through W3.3's `CustodialRecipientPreparer`**; without it the first release to a worker fails on gotcha 6 (un-opted-in recipient, 0.1 ALGO min balance per ASA, a chain error naming the ASA).
      *thesis:* D14 is still true — nothing on any layer can hold; the hold window that makes 'you own it' honest under chargebacks is unbuildable without it
      *depends on:* W3.1, W3.3
      *owns:* `oasis-sleek/Services/Hold/** (new)`, `oasis-sleek/Services/Actions/Handlers/HoldNodeHandler.cs (new)`, `oasis-sleek/Services/Actions/Handlers/ReleaseNodeHandler.cs (new)`, `oasis-sleek/Services/Actions/Handlers/RefundNodeHandler.cs (new)`, `oasis-sleek/Models/Actions/ActionEnums.cs`, `oasis-sleek/Controllers/HoldController.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/HoldLedger.cs (new) + generated schema`, `oasis-sleek/tests/.../Hold/**`
- [ ] **W3.5** (ardanova, S, risk low) — Fungible-mint wire fix: `AzoaFungibleMintRequest` matches AZOA's DTO (Total ulong + Decimals = assetScale, reserve target), response fields match; OpenAPI contract test against AZOA's spec
      *thesis:* Invisible when it works; today flipping Provider=Azoa fails every mint with 'zero total'
      *depends on:* W3.1
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaBackedAlgorandService.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaWireModels.cs`, `ardanova/.../tests/Azoa/FungibleMintContractTests.cs (new)`
- [ ] **W3.6** (azoa, M, risk high) — Implied, revocable consent grant for tenant-provisioned child users created in ProvisionChildAsync, scoped to the provisioning tenant; test that a plain tenant-key allocation against a child user with no grant is refused; resolve how AllocationController derives actingTenantId from an API-key principal. (The ArdaNova settings affordance lands in W6.1.)
      *thesis:* Either the user must log into AZOA to click 'allow', or tenant-key ops silently bypass consent — both are wrong; the exchange in W5.1 signs with the user's custodial key on tenant request
      *owns:* `oasis-sleek/Managers/TenantManager.cs`, `oasis-sleek/Controllers/TenantConsentController.cs`, `oasis-sleek/Controllers/AllocationController.cs`, `oasis-sleek/Services/Consent/TenantProvisionedGrantRegistration.cs (new)`, `oasis-sleek/tests/.../Consent/**`
- [ ] **W3.7** (azoa, L, risk high) — **Per-project custodial treasury account on the node (Q-X6 / N-X3).** Under the treasury-rate exchange the ARDA leg moves user→platform reserve and the project ASA moves reserve→user, so **the project itself receives nothing on chain** and its funding balance would exist only as ArdaNova Postgres rows — `TreasuryService.ProcessFundingInflowAsync` splitting a double into `indexFund`/`liquidReserve`/`operations` bucket balances plus `PlatformTreasuryTransaction` rows. That is the ledger-of-record-for-real-value **D47 rejected for stakes, surviving on the project side**. This item gives each project an operator-owned custodial treasury account on the node — created by the reserve service exactly as the reserve wallet is, **not** a tenant child user, so no person's KYC or uniqueness gate applies to a non-person account — with `IProjectTreasury` (resolve-or-create by tenant + project reference, address, balance per asset, sign-as-treasury via KeyCustodyService) and a node-side treasury ledger per project (credited, spent, held). Operator read/close surface. ⚠ Do not confuse this with `NodeTreasuryManager` / `NodeTreasuryDestination`, which are the node's own fee destination and are a different concept.
      *thesis:* Invisible, and it is what makes D44's "spend ARDA to FUND A PROJECT" true — after this, funding a project moves value TO the project, on chain, instead of to the operator's reserve with a database note about whose it was
      *depends on:* W3.1
      *owns:* `oasis-sleek/Services/Reserve/IProjectTreasury.cs (new)`, `oasis-sleek/Services/Reserve/ProjectTreasuryService.cs (new)`, `oasis-sleek/Services/Reserve/ProjectTreasuryRegistration.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/ProjectTreasuryLedger.cs (new) + generated schema`, `oasis-sleek/Controllers/Admin/ProjectTreasuryController.cs (new)`, `oasis-sleek/tests/.../Reserve/ProjectTreasuryTests.cs (new)`

⚠ **Collision:** `AllocationManager.cs`, `AllocationRequest.cs`, `AlgorandProvider.cs` and `CustodialRecipientPreparer.cs` belong to W3.3 only — W3.1 calls existing provider methods, and W3.4 moves value through `IValueRailProvider.TransferAsync` and calls W3.3's preparer through its public surface (declared dependency), never editing it. `AllocationController.cs` belongs to W3.6 (W3.3 stays inside the manager). `Program.cs` and `appsettings.json` belong to W3.1; W3.3/W3.4/W3.6/W3.7 register through their own `*Registration.cs` extension files which W3.1 wires. `FungibleTokenManager.cs` is W3.1 only. `ActionEnums.cs` is W3.4 only. Inside `Services/Reserve/`, ownership is per file: `IPlatformReserve`/`PlatformReserveService` are W3.1, `PlatformTokenGenesis` is W3.2, `IProjectTreasury`/`ProjectTreasuryService`/`ProjectTreasuryRegistration` are W3.7 (`ExchangeRatePolicy` and `TenantDeclaredRateStore` arrive later, in W5.1). `Controllers/Admin/`: `PlatformTokenController` is W3.2, `ProjectTreasuryController` is W3.7. **Cross-wave:** W3.1 waits for W1.4 before touching `appsettings.json`/`NODE-HOST.md`; no Wave 3 item touches `Managers/WalletManager.cs` (W1.8's) or `Managers/TenantCustodialAccountManager.cs` (W1.5's).

## Wave 4 — The `Fiat.USD` rail in AZOA: ARDA on-ramp, reversal pipeline, sidecar retired, un-park, D16 stamps, SDK ⬜

*D45: AZOA owns Stripe, scoped to fiat→ARDA and ARDA→fiat. Today the seam (`IValueRailProvider`, `SettlementProfile`, `RailAmount`, `HostedSessionFlow`) exists and no fiat provider implements it; ArdaNova's Stripe integration and the orphan sidecar are two live money paths pointing different ways; both drop every reversal event. This wave builds the rail once, in the node, with the three sidecar invariants ported, and issues ARDA from the reserve on a verified payment. The off-ramp is Wave 8 (a user cashes out after they work). AZOA-only; multi-session. ⚠ **The earlier "W4.1 may start alongside Wave 3" clause is withdrawn:** W4.1 owns `Program.cs` and `appsettings.json`, which W3.1 owns through Wave 3 and W1.4 owns through Wave 1, so running it alongside put two live owners on `Program.cs` and three on `appsettings.json`. The rail still has no chain dependency, but the wave does not start until W3.1 hands those two files over.*

- [ ] **W4.1** (azoa, L, risk high) — `FiatRailProvider` (`RailId = "Fiat.USD"`, `SettlementProfile` carrying the chargeback window, live/test environments) registered in the rail-aware factory; Stripe Checkout session creation for a user's custodial account (tenant-driven, per-intent success/cancel URLs, startup validation of Stripe config); webhook endpoint with signature verification and an idempotent inbox; the sidecar's §auth (constant-time bearer, allowlisted routing values, origin allowlist) and §webhook-mapping (RETRYABLE vs terminal → different HTTP statuses) ported; secrets in AZOA's store. Owns Program.cs for the wave and wires W4.5/W4.6's registrations.
      *thesis:* The rail is the only way a card becomes ARDA; a return page that cannot find its intent is what every sponsor would hit today
      *depends on:* W3.1 (file handoff of `Program.cs`/`appsettings.json`; no functional dependency)
      *owns:* `oasis-sleek/Providers/Fiat/FiatRailProvider.cs (new)`, `oasis-sleek/Providers/Fiat/StripeGateway.cs (new)`, `oasis-sleek/Providers/Blockchain/BlockchainProviderFactory.cs`, `oasis-sleek/Controllers/FiatCheckoutController.cs (new)`, `oasis-sleek/Controllers/StripeWebhookController.cs (new)`, `oasis-sleek/Services/Fiat/StripeWebhookInbox.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/FiatEvent.cs (new) + generated schema`, `oasis-sleek/appsettings.json (Stripe/Fiat section)`, `oasis-sleek/Program.cs`, `oasis-sleek/tests/.../Fiat/RailTests.cs (new)`
- [ ] **W4.2** (azoa, L, risk high) — On-ramp saga: `checkout.session.completed` → ARDA quantity derived at webhook time from Stripe-confirmed `amount_total` × the tenant's registered, versioned price schedule (Q-X2; never from client metadata — sidecar §pricing ported) → `IssueUnits` ARDA from the reserve into the user's custodial wallet (recipient preparation from W3.3) → operation_log row with Purpose=on-ramp and the REAL USD-at-time stamp (D16, finally honest: the fiat amount is known); tenant price-schedule registration endpoint; retryable-in-progress mapping so Stripe neither retries forever nor gives up with a customer charged and no ARDA. **Writes an on-ramp LOT row per issuance** (user, amount, source charge, `reversalWindowEnds`) — the source-aged cash-out rule in W8.1 needs per-lot provenance of a fungible ASA, and the node keeps no balance ledger (gotcha 4); W8.1 consumes lots FIFO, and ARDA received from selling units has no on-ramp source and is therefore unaged.
      *thesis:* 'Add funds' produces a balance the user can spend; the money-printer (client-chosen quantity) cannot exist
      *depends on:* W4.1, W3.2, W3.3, W3.6
      *owns:* `oasis-sleek/Services/Fiat/OnRamp/** (new)`, `oasis-sleek/Services/Fiat/TenantPriceSchedule.cs (new)`, `oasis-sleek/Services/Fiat/TenantPriceScheduleStore.cs (new)`, `oasis-sleek/Controllers/TenantPriceScheduleController.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/TenantPriceSchedule.cs (new) + generated schema`, `oasis-sleek/Persistence/SurrealDb/Models/OnRampLot.cs (new) + generated schema`, `oasis-sleek/tests/.../Fiat/OnRampSagaTests.cs (new)`
- [ ] **W4.3** (azoa, XL, risk high) — Reversal pipeline (D10): `charge.refunded`, `charge.dispute.created`, `charge.dispute.funds_withdrawn/reinstated` recorded as first-class ledger facts; compensation saga: claw back the on-ramped ARDA if still held → else the project units it bought (asset clawback, Q-X3, or refund of an open HOLD) → else platform absorbs from reserve and flags the account; partial refunds; per-flow risk policy; operator view of open reversals. Do not open the on-ramp to real cards before this lands.
      *thesis:* Stops the money-printer where a disputing sponsor keeps stake while honest backers' cap table is diluted
      *depends on:* W4.2, W3.4
      *owns:* `oasis-sleek/Services/Fiat/Reversal/** (new)`, `oasis-sleek/Services/Sagas/FiatCompensationSaga.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/FiatReversal.cs (new) + generated schema`, `oasis-sleek/frontend/src/app/(dashboard)/admin/fiat-reversals/page.tsx (new)`, `oasis-sleek/tests/.../Fiat/ReversalTests.cs (new)`
- [ ] **W4.4** (azoa, S, risk low) — Retire the competing fiat designs: delete `fiat-sidecar/` and its compose service; record the port in the sidecar's former AGENTS invariants location. **The SDK `orchestration/` deletion moved to W4.7**, which owns `package.json` (its `./orchestration` export at line 45-48) and `tsup.config.ts` (its entry at line 11) — deleting the sources here would have left the tree unbuildable between two items.
      *thesis:* Prevents the next engineer building a second Stripe path with its own pricing
      *depends on:* W4.1
      *owns:* `oasis-sleek/fiat-sidecar/** (delete)`, `oasis-sleek/docker-compose.dev.yml`, `oasis-sleek/docs/FIAT-RAIL.md (new)`
- [ ] **W4.5** (azoa, M, risk medium) — Un-park reconciliation: ReconciliationHostedService calls `ActionManager.ReconcileAllParkedRunsAsync` each cycle with a hard-stuck threshold that raises an operator alert (bridge G6 pattern); operator console for AwaitingReconciliation runs. ⚠ `ReconcileAllParkedRunsAsync` is **NEW** — only `ActionManager.ReconcileRunAsync(runId, …)` (`Managers/ActionManager.cs:2102`) exists today, which is why a parked run waits for a human to name it.
      *thesis:* An issuance whose confirmation read timed out sits 'pending' until a human calls the sweep
      *owns:* `oasis-sleek/Services/Reconciliation/ReconciliationService.cs`, `oasis-sleek/Services/Reconciliation/ReconciliationHostedService.cs`, `oasis-sleek/Managers/ActionManager.cs (reconcile-all entry)`, `oasis-sleek/frontend/src/app/(dashboard)/admin/parked-runs/page.tsx (new)`
- [ ] **W4.6** (azoa, S, risk medium) — D16 fix: `TryReadRailAmount` uses `ValueDimension.ChainAsset(SourceTokenId)`, not NativeChainAsset; `TenantDeclaredValueRateSource` that records ArdaNova's declared USD (rate + source + timestamp, nothing fabricated); Swap/Exchange ops stamped
      *thesis:* The first real rate source would otherwise stamp N project-token units as N microAlgos on the sponsor's statement
      *owns:* `oasis-sleek/Managers/BlockchainOperationManager.cs`, `oasis-sleek/Services/Analytics/TenantDeclaredValueRateSource.cs (new)`, `oasis-sleek/Services/Analytics/AnalyticsRegistration.cs (new)`, `oasis-sleek/Services/Analytics/AGENTS.md`, `oasis-sleek/Interfaces/IValueRateSource.cs`
- [ ] **W4.7** (azoa, M, risk low) — azoa-sdk parity for Waves 3-4: fiat on-ramp checkout + event types, allocation IssueUnits/Purpose, hold types, reserve/genesis reads, **project-treasury reads**, tenant custodial UnderReview; **delete `src/orchestration/**` together with its `./orchestration` export in `package.json:45-48` and its `tsup.config.ts:11` entry, in one buildable step** (absorbed from W4.4 — a consumer that wants its own bridge builds it outside this SDK); hygiene: Tinyman adapter resolves decimals from the indexer (remove `?? 6`), ALGO/ASA amounts as base-unit strings parsed with BigInt, `supportsBridging` from a capability probe — note that under D50 Tinyman is off ArdaNova's user path entirely, so this is public-consumer hygiene, not a money-path fix; decide whether ArdaNova's .NET clients are generated from the SDK; publish 0.3.x; API_SYNC
      *thesis:* None for ArdaNova users; bites any public consumer that signs client-side
      *depends on:* W4.2, W3.4
      *owns:* `oasis-sleek/sdk/azoa-wallet/src/**`, `oasis-sleek/sdk/azoa-wallet/package.json`, `oasis-sleek/sdk/azoa-wallet/tsup.config.ts`, `oasis-sleek/API_SYNC.md`

⚠ **Collision:** `Program.cs`, `appsettings.json` and `BlockchainProviderFactory.cs` belong to W4.1; W4.5/W4.6 register via their own extension files. **The whole `sdk/azoa-wallet/` tree — sources, `package.json` and `tsup.config.ts` — is W4.7's**, including the `orchestration/` deletion; W4.4 keeps only `fiat-sidecar/**`, `docker-compose.dev.yml` and the new `docs/FIAT-RAIL.md`. `ActionManager.cs` is W4.5 only. W4.3 reaches HOLD through W3.4's public surface and does not edit `Services/Hold/**`. **Cross-wave:** W4.1 takes `Program.cs`/`appsettings.json` only after W3.1 is done with them (declared dependency); nothing in this wave runs alongside Wave 3.

## Wave 5 — Project tokens: the exchange, one ASA per project into the reserve, ArdaNova's money model ⬜

*A sponsor spends ARDA and receives project units (D44) — so there must be an exchange (N-X1) and a project ASA to receive (D47). Today `ProjectTokenConfig.assetId` is never written, `CreateFungibleASAAsync` has no callers, ArdaNova's `ExchangeService`/`TreasuryService` do double/int money math against an ARDA that has no genesis, and `TokenBalance` pretends to be a ledger. This wave gives the node a treasury-rate exchange **built as an Action DAG plus an `ActionGraphBundle` (D50), not an AMM**, lets a KYC-ready owner mint their project's ASA into the reserve at publish, and turns ArdaNova's money model into a fixed-scale read-model over chain truth — including `TreasuryService`, which stops being a balance ledger and becomes a read-model of the project's treasury account on the node (Q-X6).*

- [ ] **W5.1** (azoa, XL, risk high) — Treasury-rate exchange as **an Action DAG plus an `ActionGraphBundle` (N-X1 / D50)**: a bundle definition composed from the node's existing value nodes, instantiated per request by `ReserveExchangeManager` with ArdaNova's already-decided amounts, and executed as **one atomic Algorand group**:
    - **buy leg** — ARDA **user → the project's treasury account** (W3.7), *not* to the platform reserve: the project must receive on chain, or its funding balance is the DB ledger D47 rejected;
    - **units leg** — project ASA **reserve → HOLD** (with `releaseNotBefore` = the hold window) or **reserve → user**, the reserve remaining the ASA counterparty and drawing **through a node-side partition check** on W3.1's reserve ledger so a partition cannot be oversubscribed;
    - **fees** — the reserve/fee-payer **pools the fee for every user-signed transaction in the group**; user custodial accounts never hold spendable ALGO, so without this the first real exchange fails with a chain error about ALGO on the sponsorship moment itself.
    Rate + source + version are stamped via W4.6 (**ArdaNova computes the rate, the bundle executes the move** — D7/D16); idempotent through the **existing allocation idempotency ledger**, and **`GET /api/exchange/receipt/{idempotencyKey}` is the named read surface** W6.3 reconciles against (the allocation receipt route cannot resolve an exchange op id on its own); KYC/consent-gated like every allocation. Reverse direction (project ASA → ARDA) behind an `ExchangeRatePolicy` seam whose liquidity policy lands in **W8.2, before any user can sell**. Consumer-safe errors. `SwapManager` stays DEX-only and off ArdaNova's path — the existing `Swap` node delegates to DEX adapters and is **not** this exchange; nothing is wired through `ISwapManager`.
      *thesis:* The sponsorship moment itself: spend ARDA, fund the project, hold a stake — invisibly
      *depends on:* W3.2, W3.3, W3.4, W3.6, W3.7, W4.6
      *owns:* `oasis-sleek/Managers/ReserveExchangeManager.cs (new)`, `oasis-sleek/Interfaces/IReserveExchangeManager.cs (new)`, `oasis-sleek/Services/Reserve/ExchangeBundleDefinition.cs (new)`, `oasis-sleek/Controllers/ExchangeController.cs (new)`, `oasis-sleek/Models/Requests/ExchangeRequest.cs (new)`, `oasis-sleek/Models/Responses/ExchangeReceipt.cs (new)`, `oasis-sleek/Services/Reserve/ExchangeRatePolicy.cs (new)`, `oasis-sleek/Services/Reserve/TenantDeclaredRateStore.cs (new)`, `oasis-sleek/Program.cs`, `oasis-sleek/tests/.../Exchange/**`
- [ ] **W5.2** (ardanova, L, risk medium) — Self-serve project token (Q-X4): a KYC-ready project owner declares name, unit, supply and disclosure at publish; a publish-time server job calls AZOA fungible-token create into the platform reserve via ArdaNova's tenant credential and persists `assetId` (REQUIRED — no assetId-optional path); **the same job resolve-or-creates the project's node-side treasury account (W3.7) and persists its reference, so the project has somewhere for ARDA to land before anyone can fund it**; `contributorSupply`/`investorSupply` are declared as **named partitions on the node's reserve ledger** (W3.1) and mirrored in ArdaNova, so the node — not only ArdaNova's DB — can refuse an oversubscribed draw; Funding tab stops saying 'Funding is not configured' for owner-created projects; wizard funding step
      *thesis:* The neighbour's project a sponsor wants to back is fundable without an operator hand-editing the database
      *depends on:* W3.5, W3.1, W3.7, W2.1
      *owns:* `ardanova/.../ArdaNova.API/Controllers/ProjectTokensController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectTokenService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/ProjectTokenConfig.cs`, `ardanova/.../ArdaNova.Infrastructure/Migrations/*_ProjectTokenAndTreasury (new)`, `ardanova/ardanova-client/src/components/projects/funding-tab.tsx`, `ardanova/ardanova-client/src/server/api/routers/project-tokens.ts`, `ardanova/ardanova-client/src/app/projects/create/page.tsx (funding step)`
- [ ] **W5.3** (ardanova, L, risk medium) — ArdaNova money model: fixed-scale money (decimal(18,2) / minor-unit long) on every value path, no (long)/(int) truncation; `ExchangeService` becomes the declared ARDA↔project-token rate ArdaNova registers with the node (one hop, ARDA user-facing — the review's token→USD collapse is REVERSED); `TokenBalance`/`TokenBalanceService` become a read-model of chain holdings via AZOA balance reads (ARDA balance = the user's custodial wallet, not `isPlatformToken` rows); **`TreasuryService` becomes a READ-MODEL of the project's treasury account on the node (W3.7)** — the `indexFund`/`liquidReserve`/`operations` split stops being authoritative balances and becomes a **policy view over one chain balance**, and `PlatformTreasuryTransaction` becomes a receipt row carrying the AZOA op id, not a ledger of record; `AzoaExchangeClient` as the single adapter every ArdaNova caller uses for exchange ops; strip `StripePaymentIntentId` from the non-admin investor DTO. `PayoutService` is NOT touched here (Wave 8). Lands **its own migration**, after W5.2's.
      *thesis:* Cents-level mismatches on a 'transparent' ledger destroy trust; ARDA balances stop reading 0 forever; and a project's money stops being a number only ArdaNova's database believes
      *depends on:* W5.1, W5.2, W4.7
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/ExchangeService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/TreasuryService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/TokenBalanceService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/SwapService.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaExchangeClient.cs (new)`, `ardanova/.../ArdaNova.Domain/Models/Entities/TokenBalance.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/PlatformTreasuryTransaction.cs`, `ardanova/.../ArdaNova.Application/DTOs/TokenomicsDtos.cs`, `ardanova/.../ArdaNova.Infrastructure/Migrations/*_FixedScaleMoney (new)`

⚠ **Collision:** W5.1 is the only AZOA item. `ProjectTokenService.cs`/`ProjectTokensController.cs`/`ProjectTokenConfig.cs` belong to W5.2; `ExchangeService`/`TreasuryService`/`TokenBalance*`/`TokenomicsDtos` belong to W5.3. **Migrations: two, in declared order, each owned by the item that designed its entities** — W5.2 lands `*_ProjectTokenAndTreasury`, then W5.3 lands `*_FixedScaleMoney` (W5.3 depends on W5.2). The earlier arrangement — W5.3's column types riding in W5.2's migration — required W5.2 to write a migration for entity shapes W5.3 had not designed yet. `funding-tab.tsx` and `project-tokens.ts` are W5.2 (W10.1 owns them later); `create/page.tsx` is W2.4's in Wave 2 and W5.2's funding step here.

## Wave 6 — The first sponsorship: buy ARDA, spend ARDA, hold project units — without a chain word ⬜

*The thesis moment. Under D44 the sponsor path is: 'Add funds' → AZOA `Fiat.USD` checkout → ARDA in the invisible wallet → 'Support this project' → intent locks quantity + rate → AZOA exchange op → project units in HOLD until the window elapses → SETTLED. Today ArdaNova's checkout is a Stripe-for-a-project that 409s by construction, the settlement would mint 'USD' cents as a fresh ASA, no intent reaches SETTLED, and the invest page teaches a two-hop conversion. ArdaNova-only; multi-session.*

- [ ] **W6.1** (ardanova, L, risk medium) — ARDA on-ramp in ArdaNova: `AzoaFiatRailGateway` replaces `StripeCheckoutGateway` (calls AZOA `Fiat.USD` checkout for the user's custodial account with per-intent return URLs); **`IStripeCheckoutGateway` is renamed to `IFiatRailGateway`** so W6.2's `FundingIntentService` (which injects it at lines 26 and 38) has an interface to bind to; `StripeWebhookController`, `IStripeWebhookInbox`, its `Infrastructure/Payments/StripeWebhookInbox.cs` implementation, `StripeWebhookControllerTests.cs` and the Stripe DTOs are deleted (AZOA terminates Stripe; ArdaNova reads the on-ramp op status), and `Application/DependencyInjection.cs` re-registers the renamed gateway; ArdaNova registers its price schedule with the node at startup (Q-X2); client: portfolio shows the ARDA balance from the read-model, an 'Add funds' flow with a return page and terminal states (Added / We'll email you / Something went wrong); the consent-revoke affordance from W3.6 lands on the verification page
      *thesis:* 'Add funds' is the first money screen a sponsor sees; it must never say Stripe, wallet or chain
      *depends on:* W4.2, W1.7, W5.3, W6.3
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaFiatRailGateway.cs (new)`, `ardanova/.../ArdaNova.Application/Services/Implementations/StripeCheckoutGateway.cs (delete)`, `ardanova/.../ArdaNova.Application/Services/Interfaces/IStripeCheckoutGateway.cs (rename → IFiatRailGateway.cs)`, `ardanova/.../ArdaNova.Application/DependencyInjection.cs`, `ardanova/.../ArdaNova.API/Controllers/StripeWebhookController.cs (delete)`, `ardanova/.../ArdaNova.Application/Services/Interfaces/IStripeWebhookInbox.cs (delete)`, `ardanova/.../ArdaNova.Infrastructure/Payments/StripeWebhookInbox.cs (delete)`, `ardanova/tests/ArdaNova.API.Tests/Controllers/StripeWebhookControllerTests.cs (delete)`, `ardanova/.../ArdaNova.Application/DTOs/StripeDtos.cs`, `ardanova/.../ArdaNova.Application/DTOs/StripeCheckoutRequest.cs`, `ardanova/ardanova-client/src/app/portfolio/page.tsx`, `ardanova/ardanova-client/src/app/portfolio/add-funds/page.tsx (new)`, `ardanova/ardanova-client/src/lib/commerce/on-ramp-status.ts (new)`, `ardanova/ardanova-client/src/app/settings/verification/page.tsx (consent affordance)`
- [ ] **W6.2** (ardanova, XL, risk high) — Sponsorship = exchange: the intent locks the project-unit quantity and the declared rate into typed fields; checkout gate = uniform readiness (W1.1) + project has `assetId`; on 'Support', the intent calls the AZOA exchange through W6.3's gateway with the one idempotency key `{avatarId}:funding-intent:{intentId}`; confirmed op → `ProjectInvestment` + a `TreasuryService` **receipt** of the treasury credit the exchange group already made on chain (`ProcessFundingInflowAsync` stops computing bucket balances and records the AZOA op id against the project's treasury read-model, W5.3) + Gate 1 + intent SETTLED in one transaction, with the hold row's `releaseNotBefore` surfaced as 'yours after N days'; owner/sponsor readiness lapse between click and confirmation becomes a HOLDING state that returns the ARDA (never an infinite Retry); delete the 'USD'-cents settlement and `AllocationKeys.FundingSettlement`
      *thesis:* The core promise — back a project and hold a stake — becomes real, on chain, invisibly, with the project's money on chain too
      *depends on:* W6.1, W6.3, W5.2, W5.3
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/FundingIntentService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/FundingIntent.cs`, `ardanova/.../ArdaNova.Domain/Models/Enums/FundingIntentStatus.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaFundingSettlementReadiness.cs`, `ardanova/.../ArdaNova.Application/Services/Interfaces/IFundingSettlementReadiness.cs`, `ardanova/.../ArdaNova.Infrastructure/Migrations/*_FundingIntentExchange (new)`, `ardanova/.../tests for FundingIntentService`
- [ ] **W6.3** (ardanova, L, risk high) — Live settlement gateway: `AzoaSettlementGateway` implements `ISelectedNodeSettlementCapability` around `AzoaExchangeClient` (exchange op, token units, never 'USD') + **receipt reconcile via `GET /api/exchange/receipt/{idempotencyKey}` (W5.1)** — the allocation receipt route does not know an exchange op id; persist Gross/Fee/Net from the receipt; attempt ceiling + operator alert on the outbox; `EconomicSettlementKind` gains EXCHANGE and HOLD_RELEASE; retire `AllocateFundingSettlementAsync`/`AllocateRewardAsync` Mint kinds; **removes the `IStripeWebhookInbox` registration from `Infrastructure/DependencyInjection.cs:71`, which W6.1 then deletes the implementation of** (W6.1 depends on this item, so the DI file stays single-owner); `DisabledAzoaSettlementGateway` implements the capability interface so readiness type-tests are honest
      *thesis:* Gives every sponsor settlement an AZOA operation id the ledger can reference — invisibly
      *depends on:* W5.1, W5.3
      *owns:* `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaSettlementGateway.cs (new)`, `ardanova/.../ArdaNova.Infrastructure/Azoa/DisabledAzoaSettlementGateway.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/AzoaAllocationService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/EconomicOutboxDispatchService.cs`, `ardanova/.../ArdaNova.Infrastructure/Outbox/EconomicOutboxLeaseStore.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/EconomicSettlement.cs`, `ardanova/.../ArdaNova.Domain/Models/Enums/EconomicSettlementKind.cs`, `ardanova/.../ArdaNova.Infrastructure/DependencyInjection.cs`
- [ ] **W6.4** (ardanova, M, risk low) — Invest + success pages in ARDA: quote in ARDA with a USD reference ('1 ARDA = $1.00'), 'your share of this project' (units and % behind a disclosure), one-hop preview (ARDA → project units; delete the TOKEN→ARDA→TOKEN chain), insufficient-ARDA state links to 'Add funds', 'show my name' consent checkbox, server error codes mapped to human copy, success page terminal states (Confirmed — yours after N days / We'll email you / Something went wrong) with a poll timeout; portfolio holdings component shows project units (pending / available) with no ASA id, address or hash
      *thesis:* The sponsorship screen reads as 'spend 50 ARDA, hold 50 units of this project', nothing else
      *depends on:* W6.2
      *owns:* `ardanova/ardanova-client/src/app/projects/[slug]/invest/page.tsx`, `ardanova/ardanova-client/src/app/projects/[slug]/invest/success/page.tsx`, `ardanova/ardanova-client/src/lib/commerce/funding-intent-status.ts`, `ardanova/ardanova-client/src/components/equity/equity-preview.tsx`, `ardanova/ardanova-client/src/components/equity/swap-preview.tsx (delete)`, `ardanova/ardanova-client/src/components/equity/conversion-chain.tsx`, `ardanova/ardanova-client/src/components/portfolio/holdings.tsx (new)`
- [ ] **W6.5** (ardanova, S, risk medium) — Economy-mode honesty: startup banner + health field 'economy: live / simulated / disabled' from Azoa:Mode + Algorand:Provider; Simulated defaults refused in Production; `EnableFundingCheckout` semantics documented *(the "ArdaNova API Program.cs drops Stripe options validation" clause is struck — `ArdaNova.API/Program.cs` has no Stripe reference; the item named work that does not exist)*
      *thesis:* QA cannot mistake the simulated stack for a working economy
      *depends on:* W6.1
      *owns:* `ardanova/.../ArdaNova.API/HealthChecks/EconomyModeHealthCheck.cs (new)`, `ardanova/.../ArdaNova.API/Program.cs`, `ardanova/.../ArdaNova.API/appsettings*.json`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaSettings.cs`, `ardanova/.env.example`
- [ ] **W6.6** (ardanova, S, risk low) — Backer privacy: investors endpoint gets [Authorize]; aggregate (count + total ARDA) for non-members, itemised for project members, display name not internal id; per-pledge 'show my name' consent persisted from W6.4's checkbox, with **its own migration, landing after W6.2's**
      *thesis:* A 50-ARDA pledge stops being visible to strangers
      *depends on:* W6.4
      *owns:* `ardanova/ardanova-client/src/components/equity/investor-table.tsx`, `ardanova/.../ArdaNova.API/Controllers/ProjectTokensController.cs (investors action only)`, `ardanova/.../ArdaNova.Domain/Models/Entities/ProjectInvestment.cs`, `ardanova/.../ArdaNova.Infrastructure/Migrations/*_PledgeNameConsent (new)`

⚠ **Collision:** the Stripe retirement is **W6.1's alone** — the gateway implementation and its renamed interface, `Application/DependencyInjection.cs`, the webhook controller, `IStripeWebhookInbox`, its `Infrastructure/Payments/StripeWebhookInbox.cs` implementation, `StripeWebhookControllerTests.cs` and the Stripe DTOs; **W6.3 owns `Infrastructure/DependencyInjection.cs`** and removes the one inbox registration line there as part of its own edit, so that file stays single-owner. Order follows the dependencies: **W6.3 → W6.1 → W6.2**. `FundingIntentService.cs`/`FundingIntent.cs`/readiness belong to W6.2, which binds to W6.1's renamed `IFiatRailGateway`; `AzoaAllocationService`/outbox/`EconomicSettlement*` belong to W6.3; W6.2 reaches the node only through W6.3's gateway. `ArdaNova.API/Program.cs` and `appsettings*.json`/`AzoaSettings.cs` belong to W6.5 (W1.7 finished with them). `ProjectTokensController.cs` — W6.6 edits the investors action only (W5.2 finished with the rest). **Migrations: two, in declared order** — W6.2's `*_FundingIntentExchange`, then W6.6's `*_PledgeNameConsent` (W6.6 depends on W6.4 → W6.2), rather than W6.6's column riding in a migration written before W6.4 designed it. The verification page: W1.2 finished with it in Wave 1; W6.1 owns it here.

**→ At the end of Wave 6 a non-crypto user has completed the first sponsorship.**

## Wave 7 — Work for a stake: bid, HOLD from the contributor pool, release into the worker's holding, credentials ⬜

*No path pays a worker: a worker cannot submit a bid from the client, accept creates an agreement that never advances, escrow is 501/503, the distribute path is 501 and admin-only. Under D47 the stake is project units from the reserve's contributor partition; under D14/W3.4 the escrow is a HOLD on the node; under D46 the worker is KYC-ready at accept (the review's 'move the gate to settlement' is dropped). ArdaNova-only; multi-session.*

- [ ] **W7.1** (ardanova, M, risk low) — Worker-side 'Propose terms' form calling opportunityBid.create (or accept an Application with an owner-entered award) so the existing apply flow becomes the paid path; award denominated in project units with an ARDA reference
      *thesis:* The schoolteacher can apply but never be accepted into paid work — the layer is absent on the client
      *owns:* `ardanova/ardanova-client/src/components/opportunity-detail-view.tsx`, `ardanova/ardanova-client/src/components/opportunities/bid-form.tsx (new)`, `ardanova/ardanova-client/src/hooks/use-opportunity-mutations.ts`
- [ ] **W7.2** (ardanova, XL, risk high) — Stake reservation on the node: collapse equityReward/awardAmount into one award field; AcceptBid (both parties KYC-ready, D46) creates an AZOA HOLD from the project's contributor partition with beneficiary = worker — **the oversubscription check runs on the node, against W3.1's named partitions on the reserve ledger**, because sponsor exchanges (W5.1) and contributor holds draw from the same reserve and a partition checked only in ArdaNova's DB can be overdrawn by the other caller; owner 'approve completion' → **`ReleaseIntoHold(releaseNotBefore)` (W3.4)**, which moves the units into a SECOND hold — the worker's window — because a plain Release cannot express release-into-a-hold and the two windows are different things; **the worker's window exists for tax deferral (gotcha 38: restricted-until-liquid defers the taxable event), NOT for chargeback exposure — reserve-sourced reward units were never bought with a card**, and the copy must say the right reason; delivered via `ProjectTokenService.DistributeAsync` behind project-owner authorization (un-501 allocate/pbi + distribute); TaskCommerceAgreement advances ACCEPTED → ESCROW_FUNDED (hold created) → SETTLED (released into the worker's hold); `TokenAllocation` becomes the read-model of the hold; guild bids declare a split at bid time
      *thesis:* The thesis-native payout exists in code and no user can trigger it; today an owner can accept ten bids against the same supply
      *depends on:* W3.4, W3.6, W5.2, W6.3
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/TaskCommerceService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectTokenService.cs`, `ardanova/.../ArdaNova.API/Controllers/ProjectTokensController.cs`, `ardanova/.../ArdaNova.API/Controllers/TasksController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/TaskServices.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/OpportunityBidService.cs`, `ardanova/.../ArdaNova.Domain/Models/Entities/TokenAllocation.cs`, `ardanova/.../ArdaNova.Infrastructure/Azoa/AzoaHoldClient.cs (new)`, `ardanova/.../ArdaNova.Infrastructure/Migrations/* (new)`
- [ ] **W7.3** (ardanova, M, risk low) — Escrow surface honesty: delete ReleaseDialog/EscrowDetailCard/DisputeForm tx-hash entry (release evidence comes from the hold receipt, never a form field); drop txHash* from every client contract and CreateTaskEscrowDto; escrow services map to hold create/release/refund; dispute ResolveAsync + admin action (refund returns units to the contributor partition); commerce page and task-economic-state speak 'reserved / paid / disputed'; AZOA_ACCOUNT_NOT_READY → 'Complete setup' link
      *thesis:* A sponsor paying a worker is prompted for a transaction hash; a disputing worker reaches a page with no outcome
      *depends on:* W7.2
      *owns:* `ardanova/ardanova-client/src/components/escrow/**`, `ardanova/ardanova-client/src/lib/commerce/escrow-release.ts`, `ardanova/ardanova-client/src/app/admin/disputes/page.tsx`, `ardanova/ardanova-client/src/app/tasks/[id]/commerce/page.tsx`, `ardanova/ardanova-client/src/components/tasks/task-economic-state.tsx`, `ardanova/.../ArdaNova.Application/Services/Implementations/TaskEscrowServices.cs`, `ardanova/.../ArdaNova.API/Controllers/TaskEscrowController.cs`, `ardanova/.../ArdaNova.Application/DTOs/EscrowDtos.cs`
- [ ] **W7.4** (ardanova, M, risk medium) — Credentials as one truth: membership credential grant un-501 behind project-owner authorization, issuing on chain through `POST /api/credential/issue/{recipientUserId}` via ArdaNova's dapp:manager tenant credential (custody assumed, so no deferral); XP tier derives from credential tier so User.tier and MembershipCredential.tier cannot disagree; credential UI says 'Verified' / 'Verification pending' with a 'View proof' disclosure instead of 'ASA #', 'Minted on Algorand', hashes
      *thesis:* 'Portable credential' is two DB columns that can disagree, delivered in the vocabulary the thesis forbids; also the gate on ArdaNova's own vote (Wave 9)
      *depends on:* W3.6 (tenant-driven issuance on a child user runs the consent gate before decrypt)
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/MembershipCredentialServices.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/CredentialUtilityService.cs`, `ardanova/.../ArdaNova.API/Controllers/MembershipCredentialsController.cs`, `ardanova/.../ArdaNova.API/Controllers/CredentialUtilityController.cs`, `ardanova/ardanova-client/src/server/api/routers/membership-credential.ts`, `ardanova/.../ArdaNova.Application/Services/Implementations/XPEventService.cs`, `ardanova/ardanova-client/src/components/credentials/credential-card.tsx`
- [ ] **W7.5** (ardanova, S, risk medium) — Reward dispatch hygiene: readiness lapse between accept and release becomes a HOLDING state with an attempt ceiling and operator alert on the outbox (the hold stays on the node; refund to the partition after N days, never an infinite Retry)
      *thesis:* Captured value can no longer sit in limbo because of someone else's KYC
      *depends on:* W7.2
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/EconomicOutboxDispatchService.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/OperatorAlertService.cs (new)`

⚠ **Collision:** W7.2 owns TaskCommerceService/ProjectTokenService/ProjectTokensController/TasksController/OpportunityBidService/TokenAllocation; W7.3 owns everything escrow (services, controller, DTOs, client components); W7.4 owns credentials; W7.5 owns the outbox dispatcher (W6.3 finished with it). W7.1 is client-only opportunity files. Migrations: W7.2 owns the folder this wave.

## Wave 8 — Cash out: project units → ARDA → dollars ⬜

*D44 OUT: 'people cash out with project tokens ... trade for the platform token which they can sell for fiat.' Today payouts are 503 at the controller and refused at the service, ArdaNova's ExecuteSwap is a stub, and no fiat off-ramp exists anywhere. Two hops, both invisible: the buy-back pays ARDA for project units (W5.1's reverse direction, under a liquidity policy), then the AZOA rail pays ARDA out to a Stripe Connect account (Q-X5). Rewards stay restricted until the hold window elapses, so the taxable event defers to liquidity (Q-tax, with counsel).*

⚠ **Order within the wave changed (and the items are renumbered to match).** The previous
plan had the liquidity policy depending on the ArdaNova cash-out flow, which un-503'd
"Sell units" for real users **before any policy existed** — the plan's own words for that
state are "either drains the reserve or silently refuses". The policy now lands **before**
the user-facing sell path: **W8.1** (rail) and **W8.2** (policy) are independent of each
other and both precede **W8.3** (the ArdaNova flow). ⚠ *Numbering note:* the previous
revision's W8.2 and W8.3 have swapped names; `DROPPED-FROM-REVIEW-PLAN.md` still cites the
old numbers.

- [ ] **W8.1** (azoa, XL, risk high) — ARDA off-ramp in the `Fiat.USD` rail: Stripe Connect Express connected account per user (onboarding at first cash-out — the one moment paperwork is expected; Connect business verification covers entities), `CreatePayoutTransferAsync` ported from ArdaNova; off-ramp saga: ARDA user→reserve (burn-equivalent) then transfer at the registered schedule; **source-aged rule consuming W4.2's on-ramp LOT rows FIFO** (only ARDA past the reversal window of the lot it came from; ARDA received from selling units has no on-ramp lot and is unaged — Q-X2); `transfer.reversed` handled through W4.3's ledger; operator view
      *thesis:* Closes the loop: work → stake → dollars is unreachable at every layer and the UI says so in three places
      *depends on:* W4.2 (lot ledger), W4.3, W5.1
      *owns:* `oasis-sleek/Providers/Fiat/StripeConnectGateway.cs (new)`, `oasis-sleek/Services/Fiat/OffRamp/** (new)`, `oasis-sleek/Controllers/FiatPayoutController.cs (new)`, `oasis-sleek/Persistence/SurrealDb/Models/FiatPayout.cs (new) + generated schema`, `oasis-sleek/frontend/src/app/(dashboard)/admin/fiat-payouts/page.tsx (new)`, `oasis-sleek/tests/.../Fiat/OffRampTests.cs (new)`
- [ ] **W8.2** (both, M, risk medium) — **Buy-back liquidity policy, landing BEFORE any user can sell** (was W8.3): how much ARDA is paid for project units, **drawn from the PROJECT'S treasury account (W3.7) with the platform reserve as the backstop** — the disclosure "sell requests are filled from the project's treasury" is only true if the project's treasury actually holds ARDA, which is what Q-X6 fixes; treasury-backed rate from ArdaNova's declared valuation, per-project caps, a queue when the treasury is short and the backstop is capped; ArdaNova's `TreasuryService` feeds the declared figures **as a read-model of that treasury balance** (W5.3); `ExchangeRatePolicy` enforces them on the node
      *thesis:* Without it the reverse exchange either drains the reserve or silently refuses — both teach the user a market concept
      *depends on:* W5.1, W5.3
      *owns:* `oasis-sleek/Services/Reserve/ExchangeRatePolicy.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/TreasuryService.cs`, `ardanova/.../ArdaNova.API/Controllers/TreasuryController.cs`
- [ ] **W8.3** (ardanova, L, risk high) — ArdaNova cash-out flow (was W8.2): `PayoutService`/`PayoutsController` un-503 as a two-step: (1) 'Sell units' — project units → ARDA via `AzoaExchangeClient`, **under W8.2's policy** (available units only; held units show their release date), (2) 'Withdraw' — ARDA → dollars via W8.1 with Connect onboarding on first use; fixed-scale money in PayoutService; `StripeService` Connect code deleted after the port; withdraw page and swap page become real flows in plain words; `PayoutRequest` records the AZOA op ids
      *thesis:* The worker who earned units can turn them into money without meeting a chain
      *depends on:* W8.1, W8.2, W5.3, W7.2
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/PayoutService.cs`, `ardanova/.../ArdaNova.API/Controllers/PayoutsController.cs`, `ardanova/.../ArdaNova.API/Controllers/SwapsController.cs`, `ardanova/.../ArdaNova.Application/Services/Implementations/StripeService.cs (delete)`, `ardanova/.../ArdaNova.Domain/Models/Entities/PayoutRequest.cs`, `ardanova/ardanova-client/src/app/portfolio/withdraw/page.tsx`, `ardanova/ardanova-client/src/app/swap/page.tsx`, `ardanova/ardanova-client/src/server/api/routers/payouts.ts`

⚠ **Collision:** W8.1 is AZOA fiat files only; W8.2 owns `ExchangeRatePolicy.cs` (W5.1 finished with it) and ArdaNova `TreasuryService.cs`/`TreasuryController.cs` (W5.3 finished with `TreasuryService.cs`). W8.3 owns every ArdaNova payout/swap file; `StripeService.cs` is deleted here, not edited. No two items in the wave write the same file, and W8.3 — the only one a user can reach — depends on both of the others.

## Wave 9 — Governance a member can actually use ⬜

*A member is shown vote buttons, then told 'CREDENTIAL_REQUIRED … separate authorized workflow'; an admin who does vote gets a proposal that can never reach PASSED; behind the 501, voterId and weight come from the request body. Per the Q6 assumption ArdaNova is the vote of record; AZOA's nodes stay gated until a consumer surface exists. Unchanged from the review plan.*

- [ ] **W9.1** (ardanova, L, risk high) — ArdaNova vote of record: voterId and weight derived server-side from the authenticated actor; PASSED/REJECTED/EXPIRED resolution on close/expiry; quorum as % of eligible members; close != cancel; Execute reachable; lift the project-scoped 501s; vote form disabled with plain copy until the member holds a credential (W7.4); remove 'TX:' and 'voting power' from votes-tab/proposals-tab
      *thesis:* A sponsor or worker cannot vote on a project decision today without meeting a crypto or workflow concept
      *depends on:* W7.4
      *owns:* `ardanova/.../ArdaNova.Application/Services/Implementations/GovernanceServices.cs`, `ardanova/.../ArdaNova.API/Controllers/GovernanceController.cs`, `ardanova/.../ArdaNova.API/Controllers/ProjectsController.cs (governance actions)`, `ardanova/ardanova-client/src/server/api/routers/governance.ts`, `ardanova/ardanova-client/src/server/api/routers/project.ts (governance procedures)`, `ardanova/ardanova-client/src/components/projects/proposals-tab.tsx`, `ardanova/ardanova-client/src/components/projects/votes-tab.tsx`
- [ ] **W9.2** (azoa, M, risk medium) — AZOA governance gate exit list (keep gated, close the holes): freeze GovernanceProposal metadata after the first claim in HolonManager.UpdateAsync (or snapshot options/scope/electorate/closesAt onto claim rows); proposal-rewrite attack tests; null-tenant electorate must not enfranchise the whole node; test the KYC-binding tenant-scope resolution in PersonUniquenessGate before any adapter; record the Phase 4 adversarial verdict
      *thesis:* A proposal author can move the outcome after members voted — the trust failure this audience notices first
      *owns:* `oasis-sleek/Managers/HolonManager.cs`, `oasis-sleek/Services/Governance/ConsumerGovernanceHolonTypes.cs`, `oasis-sleek/Services/Actions/Handlers/GovernanceProposalCreateNodeHandler.cs`, `oasis-sleek/Services/Kyc/PersonUniquenessGate.cs`, `oasis-sleek/tests/AZOA.WebAPI.Tests/Governance/**`, `oasis-sleek/Services/Governance/AGENTS.md`

⚠ **Collision:** W9.1 owns ProjectsController governance actions (W2.1 finished with list/publish). W9.2 is AZOA-only.

## Wave 10 — Transparency: where did the money go ⬜

*'Transparent settlement' is the thesis promise and the sponsor has no screen for it; the one plain-language ledger is admin-only. Must come after settlement rows exist (Waves 6-8) or the ledger is permanently empty. Under D44 every row now has an AZOA operation id and — because the on-ramp knows the fiat amount — an honest USD-at-time stamp. AZOA analytics still must NOT be exposed raw (base units, hashes).*

- [ ] **W10.1** (ardanova, L, risk medium) — Per-project 'Money' view from ArdaNova's own rows (FundingIntent settled, TokenAllocation released, PayoutRequest, reversals, platform fee) in ARDA with the D16 USD-at-time reference, human descriptions, `azoaOperationId` as a hidden 'verify' reference; **the project's balance shown is the treasury account's chain balance (W3.7 via W5.3's read-model), not a sum of DB rows** — the rows explain the balance, they do not constitute it; revive `TreasuryTransaction` or replace it
      *thesis:* Every sponsor hits this on day one: 'you put in 500 ARDA, the project paid 200 to a designer'
      *depends on:* W8.3
      *owns:* `ardanova/.../ArdaNova.API/Controllers/ProjectLedgerController.cs (new)`, `ardanova/.../ArdaNova.Application/Services/Implementations/ProjectLedgerService.cs (new)`, `ardanova/.../ArdaNova.Domain/Models/Entities/TreasuryTransaction.cs`, `ardanova/ardanova-client/src/components/projects/funding-tab.tsx`, `ardanova/ardanova-client/src/components/projects/money-tab.tsx (new)`, `ardanova/ardanova-client/src/server/api/routers/project-tokens.ts`
- [ ] **W10.2** (azoa, S, risk low) — Node transparency: rename to 'node economic-rules transparency' in docs; an operator-console page with a consumer; the conformance manifest kept out of any user-transparency narrative
      *thesis:* None for users; prevents misreporting operator surfaces as sponsor transparency
      *owns:* `oasis-sleek/frontend/src/app/(dashboard)/node-transparency/page.tsx (new)`, `oasis-sleek/docs/NODE-TRANSPARENCY.md`, `oasis-sleek/Managers/NodeTransparencyManager.cs (doc comments)`
- [ ] **W10.3** (azoa, M, risk low) — azoa-sdk final parity: exchange, off-ramp, hold read, node-transparency reads, fiat-reversal types; publish 0.4.x; API_SYNC; decide generated .NET clients for ArdaNova if W4.7 deferred it
      *thesis:* The 'SDK and .NET always mirrored' rule holds again
      *depends on:* W8.1
      *owns:* `oasis-sleek/sdk/azoa-wallet/src/**`, `oasis-sleek/sdk/azoa-wallet/package.json`, `oasis-sleek/API_SYNC.md`

⚠ **Collision:** W10.1 owns funding-tab.tsx and the project-tokens router (W5.2 finished with them). W10.2 and W10.3 are AZOA; W10.2 does not touch the SDK.

## Gotchas ledger → closing wave

Numbered in `GOTCHAS.md` order. *OC* = operator's own clock (appendix). *Accepted* = accepted with a name.

| # | Gotcha (short) | Closed by |
|---|---|---|
| 1 | CustodyMode=Disabled does not disable custody | **OC** — enforcement stays the operator's; **W1.8** lands the `CustodyModeResolver` seam that item must consult, and teaches the node the production mode in the first place |
| 2 | RealValueReadinessHealthCheck never reads custody | **OC** |
| 3 | D36 sign-without-export seam absent | **OC** (wording corrected in W0.3) |
| 4 | KEK loss unrecoverable, no backup drill | **OC** |
| 5 | Rotation signing outage, ephemeral marker, platform keys never rotated | **OC** |
| 6 | Fresh custodial wallet cannot receive an ASA (opt-in, min balance, error leaks) | **W3.3** |
| 7 | AwaitingReconciliation runs never re-probed | **W4.5** (its `ReconcileAllParkedRunsAsync` entry is NEW — only `ReconcileRunAsync` exists) |
| 8 | No consent grant for tenant-provisioned child users | **W3.6** |
| 9 | Chargebacks/refunds silently discarded on both sides | **W4.3** (single pipeline in the rail) |
| 10 | Static SuccessUrl; intent row committed before Stripe config resolves | **W4.1** (rail owns per-intent URLs + startup validation); ArdaNova path deleted in **W6.1** |
| 11 | Node fee shaved silently; Transfer fee>0 hard-denies | **W3.3** (quote + no denial), **W6.3** (Gross/Fee/Net persisted) |
| 12 | AllocationKind.Mint per allocation; 'USD' cents settlement | **W3.3** (IssueUnits), **W6.2/W6.3** (exchange op; Mint callers retired) |
| 13 | Fungible-mint wire model mismatch | **W3.5** |
| 14 | D16 TryReadRailAmount NativeChainAsset trap; Swap ops unstamped | **W4.6** |
| 15 | Money math double/int with truncation | **W5.3** (all value paths except PayoutService), **W8.3** (PayoutService) |
| 16 | ARDA has no genesis | **W3.2** (genesis — configuration and operations over the existing `FungibleTokenCreate` → `CreateASAAsync` path, devnet faucet as the test path, D49) + **W5.3** (ArdaNova reads chain). The review's fix ('ARDA internal-only') is REJECTED by D44 |
| 17 | SDK Tinyman decimals, BigInt, supportsBridging | **W4.7** — public-consumer hygiene only: D50 takes Tinyman off ArdaNova's user path entirely |
| 18 | Simulated defaults look like a working economy | **W6.5** |
| 19 | PostsController unauthenticated, caller-supplied identity | **W2.5** |
| 20 | Every DRAFT public; nothing publishes | **W2.1 / W2.2** |
| 21 | Wizard overwrites description; search misses the problem | **W2.1 / W2.4** |
| 22 | UserManager O(n) racy uniqueness; AzoaAvatarService email leak | **W0.5** deletes the leak; **W1.4** closes the rest — the name "no public self-register on the node" was FALSE (`Controllers/UserController.cs:24-31` is `[AllowAnonymous]` with no config gate), so W1.4 adds the `Registration:Enabled=false` Production gate and a unique index on email/username. Only then is the residual acceptable by that name |
| 23 | azoaAvatarId unique index Conflict-locks after node rebuild | **W1.6** |
| 24 | KYC approval expiry tied to SubmissionExpiryDays; second writer of verificationLevel | **W1.4 / W1.6** |
| 25 | AML non-clearance silent, no recourse | **W1.5 / W1.6** |
| 26 | Identomat requires a human first-party tenant login; empty trust profile | **W1.4** (node-level policy, D48) |
| 27 | Identomat sends no return URL; ArdaNova never notified | **W1.4 / W1.2** |
| 28 | Eligibility swallows exceptions as Forbidden; demands wallet for card actions | **W1.1** closes the swallow; the wallet demand is now CORRECT under D46 |
| 29 | Readiness lapse parks money on infinite Retry | **W6.2** (HOLDING + ARDA return), **W7.5** (rewards) |
| 30 | GovernanceService takes voterId/weight from the body | **W9.1** |
| 31 | AZOA proposal rewrite after ballots | **W9.2** |
| 32 | KYC binding scope vs ActingTenantId in PersonUniquenessGate | **W9.2** (tested before any adapter) |
| 33 | Sybil protection opt-in per operation | **Accepted — "per-person limits are AZOA uniqueness requirements, never DB counts"** (checklist note in W1.1) |
| 34 | D19 clawback on equity ASAs makes ownership conditional | **Wave 0 Q-X3** decides; **W3.4** (hold window via HOLD ledger) + **W5.2** (disclosure at mint) implement |
| 35 | Vote gate requires a credential no route can issue | **W7.4 / W9.1** |
| 36 | AcceptBid reserves nothing; tx-hash entry in ReleaseDialog | **W7.2 / W7.3** |
| 37 | Investors endpoint unauthenticated; StripePaymentIntentId leaks | **W5.3** (strip) + **W6.6** (authorize + aggregate) |
| 38 | Tax at receipt for equity-for-work | **W7.2** names it as the worker window's actual rationale, **W8.3** makes it real (restricted-until-liquid via the hold window) + **Accepted — "counsel confirms before first payout"** |
| 39 | In-flight 81-file set uncommitted | **W0.5** |
| 40 | azoa-sdk 0.3.0 unpublished; SDK bypassed on every vertical | **W0.4** (publish), **W4.7** (generated clients decision) |
| 41 | KYB: no entity screening path | **Accepted — "natural-person founders; Connect business verification at cash-out"** (**W8.1** covers entities at payout) |

Three defects the 2026-09-07 critique found have no `GOTCHAS.md` number because the review
never reached them; they are scheduled here rather than accepted: **custody gating by
omission** (`walletReady` unreachable outside Development) → **W1.8**; **ledger-first
residue on the project side** (a project's ARDA existing only as Postgres bucket balances)
→ **W3.7** with **W5.1/W5.3/W6.2/W8.2/W10.1** threading the consequence; **a live sell path
before any liquidity policy** → the Wave 8 reorder.

## Scale, honestly

**Where the first sponsorship is now: still the end of Wave 6 — but further away than the previous revision said.** Critical path: W0 → W3 → W4 → W5 → W6, with Waves 1 and 2 in parallel alongside W3. Four of those five are multi-session (W3, W4, W5, W6). Under the review's fiat-first plan the first sponsorship was at the end of its Wave 3 (three sequential waves, one multi-session). Chain-first roughly doubles the distance — and the reason is not custody posture (which is assumed) but the count of node subsystems that do not exist and that D44/D47 put in front of the first ARDA changing hands:

1. a platform reserve wallet, with named partitions per asset (W3.1),
2. ARDA genesis — **now the cheapest item on this list**: configuration and operations over the `FungibleTokenCreate` → `CreateASAAsync` path that already exists, proved on devnet through the existing faucet, not a chain integration (W3.2, D49),
3. `IssueUnits` + custodial recipient preparation — the opt-in/min-balance problem the review called the first thing a real payout would fail on (W3.3),
4. HOLD, absent on every path since D14, now with `ReleaseIntoHold` for the worker's second window (W3.4),
5. **per-project custodial treasury accounts (W3.7) — added in this revision.** Without them the exchange credits the operator's reserve and the project's money is a Postgres number; D44's "spend ARDA to FUND A PROJECT" would fund nothing on chain,
6. the `Fiat.USD` rail with the reversal pipeline — the D10 money-printer must be closed before a real card is charged, because a chargeback now has to unwind an on-chain issuance, not a DB row (W4.1-W4.3),
7. the ARDA↔project-token exchange — no longer undesigned (D50: an Action DAG plus an `ActionGraphBundle`), but still an XL build with an atomic three-leg group, fee pooling and a node-side partition check (W5.1).

**What moved, and by how much.** Two changes in this revision push the first sponsorship out, and one pulls it in:

- **Out:** Wave 3 gains a seventh item, W3.7, and it is on the critical path — W5.1 cannot credit a project that has nowhere to be credited. Call it one more L-sized node subsystem in the wave that was already the longest.
- **Out:** the previous revision's only stated schedule compression — "starting W4.1 the same week W3 opens" — **is withdrawn.** W4.1 owns `Program.cs` and `appsettings.json`, which W3.1 owns through Wave 3 and W1.4 owns through Wave 1; running it alongside meant two live owners on one file and three on another. Wave 4 now begins after W3.1 hands those files over. Waves 1 and 2 still run alongside Wave 3, and that is the parallelism that remains.
- **In:** W3.2 shrinks from a build (M) to configuration and operations (S) under D49. It does not offset W3.7.

Net: **the first sponsorship is one L-sized node subsystem and one lost overlap further out than the previous revision claimed** — the wave it lands in has not changed, the distance to that wave has.

**Money in before money out — say it plainly.** The on-ramp opens at Waves 4/6; the off-ramp is Wave 8, two multi-session waves later. **Between them, buying ARDA is a one-way door:** a sponsor who buys ARDA cannot turn it back into dollars, and their only exit is a Stripe refund through W4.3's reversal pipeline. Two honest options, and the owner must pick one rather than letting it happen by default: (a) **hold the real-card on-ramp closed until W8.1 exists** — test cards only until the loop closes; or (b) **open it and accept, by name, "no cash-out until Wave 8; the exit is a refund through W4.3"**, disclosed to sponsors in those words. This plan assumes (b) unless the owner says otherwise; it is recorded in "Accepted residual risks" below.

**Two clocks this plan cannot bound.** Wave 0's four open decisions (Q-X2 through Q-X5) are the owner's, and **Stripe Connect platform approval** (Q-X5) is an external review with no schedule this plan can set. Both should start at Wave 0, not when their wave arrives; Connect approval arriving late does not delay Waves 1-7, but it delays W8.1 by exactly as long as it takes.

Only after those seven subsystems does ArdaNova's cutover (W6) start, and W6 is itself a rewrite of the funding intent, the settlement gateway and the invest pages. What the chain-first order buys in exchange: there is no second migration later — the review's fiat-first plan had a Wave 9 that re-platformed every ledger row onto ASAs after users already held them; that wave no longer exists, and after this revision there is no ledger-of-record-for-real-value left on either side of the boundary. A worker paid in stake is Wave 7; dollars out is Wave 8; the review's "roughly a quarter of engineering before a real sponsor can pay a real worker" now reads as: roughly a quarter before the first sponsorship, and the worker-paid moment a further two multi-session waves after that.

## Accepted residual risks

Everything the 2026-09-07 critique raised is applied above except the following, which are
accepted with a name and an acceptor. *Owner* = the node operator; *plan* = accepted by
this document's author and reversible at the named wave.

- **No cash-out until Wave 8; the exit is a refund through W4.3.** *Accepted by: owner, at Wave 0.* If the owner picks option (a) in "Scale, honestly" instead, W4.1's live-card enablement moves behind W8.1 and this residual disappears. Until they choose, the plan proceeds on (b) and the disclosure copy in W6.1/W6.4 must say so.
- **`DROPPED-FROM-REVIEW-PLAN.md` still cites the pre-renumber Wave 8 items** ("new W8.1", "new W8.2") and does not know about W1.8, W3.7 or the D49/D50/D51 fold-in. *Accepted by: plan.* That file is a historical record of what the chain-first re-sequence dropped, it is not owned by this revision, and rewriting it would blur what was actually dropped in the first pass. The renumbering note is stated inline in Wave 8.
- **`GOTCHAS.md`, `DECISIONS-TO-RATIFY.md` and `REVIEW-EVIDENCE.json` still use the pre-D51 names for `ActionGraphBundle`.** *Accepted by: plan.* The rename is being applied by a parallel workflow across the repo; this plan uses `ActionGraphBundle` throughout and those three files are not this revision's to edit.
- **Gotchas 33 (Sybil protection opt-in per operation), 38's tax question and 41 (KYB entity screening) stay accepted as they already were** — see the ledger rows for their names and acceptors. Nothing in this revision changes them; W7.2 only corrects *why* the worker's hold window exists.
- **Q-X6 is answered without the owner in the room.** *Accepted by: plan; reversible by owner before W3.7 lands.* The plan could not be collision-proofed with the question open — W3.7, W5.1's destination leg, W5.3's read-model and W8.2's disclosure all depend on the answer. Reversal cost is medium and is stated at Q-X6.
- **W1.8 recognises a production custody mode; it does not assure one.** *Accepted by: owner.* Float caps, the Production boot guard, `RealValueReadinessHealthCheck` reading all three legs and `CustodyMode=Disabled` genuinely disabling remain on the operator's clock (appendix). The risk this leaves open is a node that *can* custody in Production before the operator has finished proving it *should* — W1.8 deliberately does not close it, because closing it is custody assurance and D48 puts that outside this program.
- **The collision proofs are hypotheses.** *Accepted by: plan.* They were computed against `2cd6616` and this revision moved items between waves; AC3 already requires a real grep at each wave launch, and that requirement is now load-bearing rather than ceremonial.

## Appendix — Operator's own clock (NOT scheduled, D48)

These are the owner's responsibility as node operator and custodian of record, handled outside this program. They are listed so nobody re-imports them into a wave. None gates any item above.

- **Sign-only key handle** (old W8.1): replace `IPlatformKeyDerivation.TryDerive(secret, out byte[])` with `ISigningKeyHandle { SignAsync, PublicKey }` per chain; per-wallet DEK envelope in `WalletKeyService` with a Previous-key window; `ITransactionSigner` takes a handle. Gotchas 3, 4, 5.
- **Custody ASSURANCE: Production boot guard + honest readiness probe + float caps** (old W8.2, minus its enabling half): `CustodyMode` must be Disabled or a registered adapter and `CustodyMode=Disabled` must actually disable; KEK ≥ 32 chars and not a committed dev value; rotation marker on a persisted volume; `RealValueReadinessHealthCheck` reads all three legs; per-user and total-float caps as `AllocationManager` invariants. ⚠ **This item consumes W1.8's `Services/Custody/CustodyModeResolver.cs` rather than re-deriving the predicate** — W1.8 is the scheduled *enabling* half (the node recognising a production mode at all, so `walletReady` can ever be true), and this is the *assurance* half. Gotchas 1, 2.
- **KEK backup/escrow drill and platform-key rotation runbook** (old W8.5 first half): NODE-HOST.md sections; new-account + ASA role-transfer script. Gotchas 4, 5.
- **D27 vs D36 reconciliation and the KMS/HSM vendor decision**: the operator closes it; the RUNBOOK records only that it is open and whose it is (W0.3 wording).
- **Choosing the production `CustodyMode` value and placing the KEK secret** for the node: the operator picks the value and sets it as a Railway variable on the AZOA service. ⚠ **This no longer gates W1.7.** The previous revision sequenced it "before W1.7 flips `EnableCustodialAccounts`", which quietly made a scheduled wave depend on an unscheduled operator action for a code path that does not exist: no non-development `CustodyMode` is recognised anywhere in the tree today. **W1.8 implements the recognition and W1.7 depends on W1.8**; what remains here is only the operator's choice of value and where the secret lives, which W1.7's startup validation names if missing.
