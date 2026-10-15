---
type: Track Spec
title: Launch readiness — QA-ready on main, both repos
description: Close every open item the production-readiness program left, wire the money loop end to end, add the QA harness that proves the golden path, and land it on main in both repos.
tags: [launch, launch-readiness_20260909, in-flight, qa, merge-to-main]
timestamp: 2026-09-09T00:00:00Z
resource: ./metadata.json
---

# Launch readiness

## Why this track exists

The 11-wave production-readiness program (`ardanova-production-readiness-waves_20260907`)
delivered 53 of 54 items across 18 commits and **closed with 95 open items**, six waves at
FAIL under adversarial review, and a money loop that refuses honestly at four seams rather
than running. That was the correct outcome for that program — it was scoped to *build the
system*, and its fail-closed stubs are why nothing moves value wrongly today. It is not a
launch.

This track is the launch. It exists because the owner asked for one program, designed
end to end, after which the product is **QA-ready**: the golden path runs against Simulated
providers, every `OPEN-ITEMS.md` row is closed, refuted with evidence, or converted to a
scripted operator step, and `main` carries the result in both repositories.

Two things this track corrects in *how* work is orchestrated, learned the hard way on the
prior program:

1. **A FAIL verdict must trigger remediation, not a note.** The prior program recorded
   verdicts and moved on — the house defect ("a claim in prose is not a control in code")
   reproduced in the orchestration itself. Every wave here runs remediate → re-review, up
   to twice, before it closes.
2. **Resume caches the longest unchanged *prefix*.** Inserting a stage mid-script and
   resuming re-runs everything after the insertion point. The remediation loop is in the
   script from the first line this time.

## The objective, stated once

A non-crypto sponsor signs in, finishes account setup (identity + KYC + an invisible
custodial wallet), adds funds with a card, sponsors a problem-solver's project, watches a
worker take a stake, and later cashes out — **without meeting a wallet address, a mnemonic,
a gas fee, a chain name, a signed challenge, or the word "token" where "funds", "stake" or
"units" would do.** ARDA is backing-derived (1 ARDA = $0.01 at genesis, `Decimals = 2`),
so every amount a person sees is a dollar figure with ARDA as a caption at most.

## Waves

| Wave | Purpose | Gate to close |
|---|---|---|
| **L0** | Stop the bleeding — every CRITICAL on the shipped configuration, the two runtime blockers (Prisma drifts, `PayoutRequest` migration), the draft-project leaks, the reserve tenant guard | No CRITICAL open; both repos build; the settlement row can be written |
| **L1** | Open the valve, AZOA side — partitions declarable, treasuries resolvable, supply widened, issuance scoped correctly, a real atomic exchange submitter with receipt payload and compensation, the pause enforced at both dispatch seams | An exchange settles on Simulated; `DeclarePartitionsAsync` has a live caller |
| **L2** | Wire ArdaNova to the node — project provisioning, add-funds HTTP surface, cash-out transport, credential issuance, offering price, the orphaned card rail deleted | A project becomes fundable; a sponsor obtains units; a payout leaves |
| **L3** | Thesis sweep — dollars primary, no chain word on any member surface, a vocabulary control that fails the build on a hit | The vocabulary test is green with an explicit operator-only allowlist |
| **L4** | Governance of record + hygiene — author-close hole, third write seam, frozen question, the separate-context review that lifts `NotEnabledActionNodeTypes` | Independent PASS verdict on `Services/Governance`, or findings recorded |
| **L5** | QA readiness — every owed test, the three assertion-red suites fixed not relaxed, full `azoa.sln` including integration, a React component harness in `ardanova-client`, an E2E golden path, QA runbook + smoke checklist, docs synced | Full suites green in both repos; golden path passes end to end on Simulated |
| **L6** | Operator + deploy readiness — Railway template corrected, every new config key with a safe default, scripted (not run) operator procedures for tenant provisioning and on-ramp declaration, external dependencies named | Production boot from the template does not throw; each operator step is a script |
| **L7** | Merge-to-main sweep, both repos — AZOA `--no-ff` merge; ArdaNova hardening fast-forward, then the financial-domain **cherry-pick** (not merge) with generators re-run from DBML; push only when main is green | `main` builds and tests green in both repos and is pushed |

Waves are sequential. Items within a wave run in parallel with disjoint file ownership;
the orchestrator splits any collision into a serialised sub-round by string comparison,
not by an agent's assurance.

## Decisions this track makes

- **D52 — the ask is entered in dollars; ARDA is the derived caption.** Rules `OI-19`.
  Follows from the settled rate base and the thesis; W2.4's "denominated in ARDA with a USD
  reference" is reversed.
- **D53 — the financial-engine-domain branch lands by cherry-pick, never by merge.** Its own
  handoff (`ardanova-financial-domain/conductor/tracks/financial-engine-domain-hardening/PASSOFF.md`)
  mandates replaying `0bb1ec3` and `a6c3ecc` onto current main and regenerating every
  artifact in the DBML → Prisma → Zod → C# chain as one contract. `git merge-tree` reporting
  zero conflicts is not evidence of safety there; it is where the silent conflict lives. No
  financial migration is written or applied in this track — the model lands inert, per that
  handoff's stopping gate.
- **D54 — governance node types lift only on an independent verdict.** `OI-50` is a review
  item, executed in a context that did not write the code. On FAIL the three types stay in
  `NotEnabledActionNodeTypes` and the findings are recorded; nothing is lifted "because the
  tests pass".
- **D55 — operator actions become scripts, not prose.** Tenant provisioning (`OI-09`),
  on-ramp declaration (`OI-45`), mnemonic rotation (`OI-77`) each ship as a runnable script
  under `scripts/ops/` with the operator as the one who runs it. A runbook paragraph is not
  a control.

## Acceptance criteria

- **AC1** Every one of the 95 `OPEN-ITEMS.md` ids is closed with a `file:line`, refuted with
  a cited `file:line`, or converted to a script under `scripts/ops/` — none survives as prose.
- **AC2** An E2E test drives the golden path (sign-in → setup → add funds → sponsor → work
  award → cash out) to completion against Simulated providers, in `ArdaNova.Application.Tests/E2E/`.
- **AC3** Each client has a vocabulary control: a test that scans rendered user-facing
  strings for the chain-word list and fails on a hit, with an explicit allowlist file for
  operator-only surfaces. It is green.
- **AC4** `dotnet test azoa.sln` (unit **and** integration) is green with numbers recorded;
  ArdaNova's full .NET and client suites are green; the three previously assertion-red suites
  are fixed, not relaxed.
- **AC5** `main` in both repos contains the work, builds, tests green, and is pushed.
  ArdaNova main carries the two financial-domain implementation commits by cherry-pick with
  regenerated artifacts; the financial branch is retired.
- **AC6** No wave closes on a FAIL verdict without a recorded remediation → re-review cycle.
  Anything still open after two cycles appears in this track's own open-items file.
- **AC7** Zero new warnings against the 23-warning baseline in AZOA.

## What this track does not do

- Publish `azoa-sdk` — that needs the owner's npm 2FA code; `OI-01` is conditional on the
  registry already holding 0.4.0 and otherwise records itself as blocked-on-operator.
- Obtain Identomat credentials, Stripe Connect approval, or provision ArdaNova's tenant on
  the live node. Each becomes a script or a named external dependency (`L6`).
- Apply any financial-domain migration (D53).
- Deploy. Pushing `main` triggers Railway's git-connected staging deploy; that is the
  extent of it, and only when green.

## Assumptions (ordered by reversal cost)

1. Simulated providers are sufficient for QA-ready. *Reversal:* a devnet run is a later
   track; nothing here prevents it.
2. `main` is the integration branch on both repos from this date. *Reversal:* trivial.
3. The 15 generated-chain files can be regenerated from DBML without a generator change.
   *Reversal:* M2b becomes a generator fix first; the item says so if it hits it.
4. The three assertion-red suites are red for the reasons `OI-91` states. *Reversal:* L5
   finds a fourth reason and records it.
