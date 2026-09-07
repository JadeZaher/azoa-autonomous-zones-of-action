---
type: Track Spec
title: ArdaNova production-readiness waves
description: The dependency-ordered program that takes ArdaNova-on-AZOA from "no thesis moment works outside development" to a non-crypto user buying ARDA, funding a project on chain, working for a stake, voting and seeing where the money went.
tags: [feature, ardanova-production-readiness-waves_20260907, pending, cross-repo, thesis, chain-first]
timestamp: 2026-09-07T00:00:00Z
resource: ./metadata.json
---

# ArdaNova production-readiness waves

## The thesis this track serves

ArdaNova is a **social platform**, not a blockchain product. A person presents a
problem and their solution with as much clarity and context as possible, asks the
community for sponsorship through investment, brings in workers who do the work
for a stake, and tokens support an internal economy. The audience should get the
**benefits** of blockchain — real ownership, transparent settlement, portable
credentials — without ever being forced to know the blockchain space exists. If a
user must learn a crypto concept to sponsor or get paid, that is a defect.

## Where it actually stands (2026-09-07 review, 11 agents, 147 findings)

Far. The vocabulary is mostly right — a person signs in with Google, sees 'Account setup', 'Identity check', and 'Managed payment account', and never meets gas, chains, signing, or seed phrases on ArdaNova's screens — and the substrate underneath is careful (single custody chokepoint, fail-closed gates at both dispatch seams, idempotent allocations, honest 'unpriced' analytics). But no thesis moment works end to end in any non-development environment: account setup returns 409 (EnableCustodialAccounts=false, CustodyMode=Disabled, Kyc:Provider=unavailable); a project cannot be edited, published privately, illustrated, or found past the first 50; sponsorship checkout is unreachable by construction (the only settlement gateway is a Retry stub, no project ever gets an assetId, the settlement would mint 'USD' cents as a fresh ASA, and no intent can reach SETTLED); a worker cannot submit a bid from the client, an accepted agreement never advances, escrow is 501/503, the thesis-native off-chain distribute is 501 and admin-only, and payouts are refused at controller and service; voting is a browser alert or a credential nobody can issue; and 'where did the money go' is an admin-only page. Where the UI does function it teaches ARDA, project units, per-ARDA rates, Algorand addresses, transaction hashes, and a Pera/WalletConnect picker in the sidebar. The build order is inverted against the thesis — three chains and a bridge shipped fail-closed while fiat in/out does not exist — and the two repos carry contradictory docs (self-sovereign vs custodial, sidecar vs direct Stripe, a Sybil hole already fixed, tracks marked complete that are 501).

## The path taken (owner decisions D44-D51, 2026-09-07)

The review proposed a **fiat-first** path — put stake in ArdaNova's own ledger with
Stripe in and Stripe Connect out, and let AZOA's chain become an invisible anchor
only after custody was worth trusting. **The owner rejected it.** The path is
**chain-first and token-native**:

- **D44** — fiat is only the on-ramp and off-ramp for the platform token. IN: fiat →
  **ARDA** → spend ARDA to fund a project → receive **project tokens**. OUT: project
  tokens → ARDA → fiat. Sponsorship is not a Stripe checkout for a project; it is
  spend-ARDA, receive-project-ASA, on chain. ARDA is user-facing, not internal.
- **D45** — AZOA owns the Stripe rail, scoped to the ARDA on-ramp and off-ramp.
  ArdaNova's Stripe path and the orphan sidecar both retire.
- **D46** — KYC is uniform: full document KYC **and** a provisioned custodial wallet
  before any value action, sponsor and recipient alike. The wallet stays invisible;
  the identity check does not.
- **D47** — a stake is a real per-project ASA from day one, minted into a platform
  reserve. `AllocationKind.Mint`-per-reward is rejected; `TokenBalance` becomes a
  read-model of chain holdings, never a ledger of record.
- **D48** — the owner runs the AZOA node and is operator, custodian of record, and
  owner of compliance and completeness. ArdaNova is a tenant. Custody **posture** is
  assumed present and is not scheduled; custody **recognition** — teaching the node a
  production `CustodyMode` so `walletReady` can ever be true outside Development — is
  scheduled, as W1.8.
- **D49** — ARDA genesis is configuration and operations over the mint path that
  already exists (`FungibleTokenCreate` → `CreateASAAsync`), with the devnet faucet as
  the test path. Not a new chain integration. The one thing still open is the ARDA
  price per US cent.
- **D50** — the ARDA↔project-token exchange is **an Action DAG plus an
  `ActionGraphBundle`, composed from AZOA's existing primitives — not an AMM**.
  Tinyman leaves ArdaNova's user path.
- **D51** — the deployable composition unit is renamed `ActionGraphBundle` (not
  `ActionGraph`, which names a different concept: a field on `DappCompositionRequests`).

One defect the review's own model would have carried forward is closed here too: under
a treasury-rate exchange the ARDA leg moved user→platform reserve, so **a project
received nothing on chain** and its funding balance survived only as ArdaNova Postgres
rows — the exact ledger-of-record D47 rejected for stakes, surviving on the project
side. Each project now gets a **custodial treasury account on the node**, and the
exchange group credits it.

### Can a non-crypto user complete each feature today?

| Vertical | Verdict |
|---|---|
| Identity & onboarding — registration, OAuth (D17/D18), Identomat KYC + | No — but not for the reason the census feared. The user-facing words are right (Google sign-in, "Account setup", "Identity check", "Managed payment account"; no seed phra… |
| Presenting a problem and its solution (project creation, editing, disc | Partly. The text core works and is thesis-clean: a person with no crypto knowledge can sign in, fill Problem / Proposed solution / audience / impact / timeline in a five-… |
| Sponsorship through investment — fiat money path from ArdaNova funding | No. A non-crypto sponsor cannot complete a sponsorship today, and the failure has nothing to do with crypto literacy — the path is cut in four places before the UX even g… |
| Bringing in workers for a stake — task bounty → bid → escrow/hold → se | No. A worker cannot be paid in stake today by any route, on HEAD or with the in-flight hardening applied: bid-accept creates an agreement that never advances past ACCEPTE… |
| Internal token economy: ARDA token, per-project equity ASAs, project-t | No. A non-crypto user cannot complete any leg of the loop today. Sponsor: checkout is blocked by construction (readiness needs a gateway capability that no implementation… |
| Community governance (proposals, electorate, casting a vote, tally) —  | No. A sponsor or worker cannot vote on a project decision today on either system without meeting a crypto or workflow concept. On AZOA, voting is not an API at all: it is… |
| Custody and value safety — CustodyMode, KMS seam (D36), KeyCustodyServ | No. A non-crypto ArdaNova user cannot sponsor a project, escrow a bounty, or get paid for work today — and where the pipes do exist, the first live failure would teach th… |
| Transparency for a social-democrat audience: sponsor-visible money tra | No. A sponsor today gets a Stripe checkout, a success page that polls 'SETTLEMENT PENDING' forever (the only AZOA settlement gateway is a disabled Retry stub, in-flight),… |

## What this track contains

- **`plan.md`** — **11 waves, 54 items**, collision-proofed per wave, ordered
  chain-first by what a non-crypto user hits first: decisions and honest docs →
  account setup that works → presenting a problem → **the node's value primitives**
  (reserve, ARDA genesis, project treasuries, IssueUnits, HOLD, consent) → the
  `Fiat.USD` rail → project tokens and the exchange → **the first sponsorship** →
  work for a stake → cash out → governance → transparency.
- **`GOTCHAS.md`** — 41 traps that will bite in production, each cited and with its fix.
- **`DECISIONS-TO-RATIFY.md`** — 16 product decisions code has already made implicitly.
- **`DROPPED-FROM-REVIEW-PLAN.md`** — every item the chain-first re-sequence dropped or
  reshaped, and why. ⚠ Written before the 2026-09-07 critique pass, so it cites the
  pre-renumber Wave 8 item names and does not know about W1.8 or W3.7.
- **`RESEQUENCE-CRITIQUE.json`** — 25 adversarial findings against the first chain-first
  plan (2 blockers). All applied or accepted by name in `plan.md`.
- **`REVIEW-EVIDENCE.json`** — the full structured review, verbatim.

## Owner questions

The review's seven questions are closed: **Q1-Q4 are answered by D44-D47**, and
**Q5-Q7 are carried as stated assumptions**, each reversible at its wave. D44 opened
five further design questions; **D49 and D50 have since answered two** of them (ARDA
genesis, the exchange mechanism), which `plan.md` records as design notes N-X1 and
N-X2. **Four remain open and gate Wave 3:**

- **Q-X2** — the ARDA price per US cent, and whether it is a rail parameter or
  consumer economics under D7/D16. The last unanswered piece of the on-ramp.
- **Q-X3** — project-ASA parameters (supply, decimals, roles, the clawback
  disclosure).
- **Q-X4** — who may create a project token.
- **Q-X5** — the ARDA → fiat off-ramp destination. ⚠ Stripe Connect **platform
  approval** is an external clock; start it at Wave 0, not Wave 8.

A fifth, **Q-X6** (who holds a project's ARDA), is **answered in the plan** — a
per-project custodial treasury account on the node — because the waves could not be
collision-proofed with it open. The owner may reverse it before W3.7 lands; reversal
cost is medium.

Answers are recorded as numbered decisions in `conductor/RUNBOOK.md` (D44+). Until
answered, the gated waves do not launch.

## Acceptance

- **AC1** A non-crypto user can sign in, **buy ARDA with a card, spend ARDA on a
  project and receive that project's units**, take a task for stake, cash out, vote,
  and see a plain-language ledger of where the money went — in a non-development
  environment — without meeting a wallet, address, chain, seed phrase, signature or
  gas fee. (A card never sponsors a project directly; it buys ARDA. D44.)
- **AC2** Every gotcha in `GOTCHAS.md` is closed or explicitly accepted with a name,
  and every finding in `RESEQUENCE-CRITIQUE.json` is applied or listed under
  `plan.md` § "Accepted residual risks" with its acceptor.
- **AC3** Every wave's collision proof is re-verified with a real grep at launch; the
  reviewer's proof is a hypothesis pinned to `2cd6616`, and the 2026-09-07 revision
  moved items between waves.
- **AC4** No value of record lives in a database row on either side of the tenant
  boundary: a user's stake is a project ASA, a user's ARDA is a chain balance, and a
  **project's** ARDA is its treasury account on the node — never a `TokenBalance`,
  `PlatformTreasuryTransaction` or bucket balance. (D47, extended to the project side
  by Q-X6.)
