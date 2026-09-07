---
type: Track Spec
title: ArdaNova production-readiness waves
description: The dependency-ordered program that takes ArdaNova-on-AZOA from "no thesis moment works outside development" to a non-crypto user sponsoring, working, voting and seeing where the money went.
tags: [feature, ardanova-production-readiness-waves_20260907, pending, cross-repo, thesis]
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

Far. The vocabulary is mostly right — a person signs in with Google, sees 'Account setup', 'Identity check', and 'Managed payment account', and never meets gas, chains, signing, or seed phrases on ArdaNova's screens — and the substrate underneath is careful (single custody chokepoint, fail-closed gates at both dispatch seams, idempotent allocations, honest 'unpriced' analytics). But no thesis moment works end to end in any non-development environment: account setup returns 409 (EnableCustodialAccounts=false, CustodyMode=Disabled, Kyc:Provider=unavailable); a project cannot be edited, published privately, illustrated, or found past the first 50; sponsorship checkout is unreachable by construction (the only settlement gateway is a Retry stub, no project ever gets an assetId, the settlement would mint 'USD' cents as a fresh ASA, and no intent can reach SETTLED); a worker cannot submit a bid from the client, an accepted agreement never advances, escrow is 501/503, the thesis-native off-chain distribute is 501 and admin-only, and payouts are refused at controller and service; voting is a browser alert or a credential nobody can issue; and 'where did the money go' is an admin-only page. Where the UI does function it teaches ARDA, project units, per-ARDA rates, Algorand addresses, transaction hashes, and a Pera/WalletConnect picker in the sidebar. The build order is inverted against the thesis — three chains and a bridge shipped fail-closed while fiat in/out does not exist — and the two repos carry contradictory docs (self-sovereign vs custodial, sidecar vs direct Stripe, a Sybil hole already fixed, tracks marked complete that are 501). The honest path is fiat-first: fix the words and docs, make identity real, finish the presentation product, put stake in ArdaNova's own ledger with Stripe in and Stripe Connect out, and let AZOA's chain become an invisible anchor only after custody is worth trusting. Waves 3, 5, 8 and 9 are each multi-session; roughly a quarter of engineering before a real sponsor can pay a real worker, and the seven owner questions must be answered before Wave 1 starts.

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

- **`plan.md`** — 10 waves, 46 items, collision-proofed per wave, ordered by what a
  non-crypto user hits first: onboarding → presenting a problem → sponsorship →
  sponsorship made safe → work-for-stake → governance → transparency → custody →
  chain anchor.
- **`GOTCHAS.md`** — 41 traps that will bite in production, each cited and with its fix.
- **`DECISIONS-TO-RATIFY.md`** — 16 product decisions code has already made implicitly.
- **`REVIEW-EVIDENCE.json`** — the full structured review, verbatim.

## Owner questions

7 questions gate waves. Their answers are recorded as numbered decisions in
`conductor/RUNBOOK.md` (D44+). Until answered, the gated waves do not launch.

## Acceptance

- **AC1** A non-crypto user can sign in, sponsor a project with a card, take a task
  for stake, vote, and see a plain-language ledger of where the money went — in a
  non-development environment — without meeting a wallet, address, chain, seed
  phrase, signature or gas fee.
- **AC2** Every gotcha in `GOTCHAS.md` is closed or explicitly accepted with a name.
- **AC3** Every wave's collision proof is re-verified with a real grep at launch;
  the reviewer's proof is a hypothesis pinned to `2cd6616`.
