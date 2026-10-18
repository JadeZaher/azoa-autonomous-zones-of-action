---
type: deferral-ledger
round: 4
status: open
owner-decision: 2026-10-17 — "merge push to main, allowing further works and loops to be deferred after a live QA"
---

# Round 4 · post-QA deferral ledger

The owner chose to push `main` for **live QA on Railway** (wait-for-CI turned off by the owner) before the
integrated sweep. Everything below is **carried forward, not dropped** — each line has a trigger and a check.
Closing vocabulary: *Built* = compiled + unit-covered; *Authored* = compiles, never executed; *Done* = executed evidence.

## Release blockers (must close before real users or real money)

| Item | Why | Closure check |
|---|---|---|
| **Enable Railway "Wait for CI"** on every AZOA service | It was found OFF (`checkSuites: false`) on 2026-10-18 — earlier deploy stalls were an expired Railway trial, not the CI gate. While off, any push to `main` deploys unreviewed code | Railway service settings show wait-for-CI on |
| **Integrated sweep, once** (plan §8: P-1…P5, ranked gates KYC/KYB → Stripe → exactly-once → rest, ≤3 failure batches) | Most round-4 tests are *Authored* only; last full CI (`e0deb11`) had 204 unit failures before the fixes | CI green on `main` |
| **CI green on `main`** (dotnet tests, integration on SurrealDB, HTTP flows, SDK, frontend incl. browser slices, E2E, schema job + upgrade step, perf budgets, gitleaks) | Railway gate and the plan's definition of done | GitHub Actions all green |
| Live **Identomat drill** with owner configs (`Kyc__Identomat__ConfigId`, `Kyb__ConfigId`, new `Kyc__Identity__PersonKeySecret`, callback URL registered) | KYC/KYB are *Built* on vendor-doc fakes only; Identomat's undecided `/result/` shape is unconfirmed | approve/abandon/reject/replay KYC, approve/reject KYB, one lost-response recovered by poll |
| **Stripe test-mode drill** (checkout `4242…` → CLI-signed webhook → credit; refund; dispute `4000000000000259` → reversal → clawback; Connect transfer; payout success/failure; tenant-funded payout debit + uncovered refusal) | Fiat is *Built*; no live rail run | drill log in the track |
| **Production activation pins** (D-7 DEX/exchange per chain, `Wtt:Enabled`, webhooks, child credentials) flip only on their named green gate | All off in production by default; deployed-host acknowledgement required (`AZOA:Activation:DeployedHostAcknowledged`) | plan §8 gate 5 record |

## Engineering carried forward

| Item | Source | Trigger to revisit |
|---|---|---|
| **D-10 provenance on transfer — not built** (design + full path inventory in `round4-lane-fix-money2.md` §Deferred; needs a post-confirmation hook in `BlockchainOperationManager.ExecuteTransferAsync`). The fix-money conservative floor rule stays in force; RV-L3 R2-1/R2-2 remain open | owner D-10 | before tenant-priced on-ramps go live |
| RV-L3 R2-3: index `on_ramp_lot.created_at` + fail-closed limit on the floor scan | fix-money2 | with D-10 |
| RV-L5 re-check R1: owner cancel of an unsigned client-signed Ethereum plan (own store CAS on signed-bytes-absent + nonce release; plan view/stuck list name the blocking nonce; SDK parity) — stuck nonce, no money at risk | fix-gates2 §NOT DONE | post-QA |
| RV-L5 re-check R2 (LOW): WTT takeover run-stopped check uses plain reads | fix-gates2 | post-QA LOW sweep |
| Pass the caller-verified owner into `IHolonStore`/`IWalletStore.DeleteAsync` (interface change, ~8 implementers) | fix-core2 §NOT DONE | post-QA |
| fix-frontend2 follow-ups: SDK unit tests for `resolvePayoutAccountSetup` / `isRecentOperatorLoginRequired` / required `proveWalletOwnership` message; row-scoped stuck-setup button (needs a signal on `FiatPayoutView`) | `round4-lane-fix-frontend2.md` | post-QA |
| WTT six-route protocol fixtures: first run, Solana registration sequence, per-route execution | `round4-lane-verification-fixtures.md` | before `Wtt:Enabled` in production |
| Real child-JWT + wallet-signature HTTP flows; L5 LOW-5 NFT real-DB fixtures; store-family gaps in `Round4Gates` inventory | `round4-lane-L6-verification.md` | sweep batch 1 |
| Wallet-ownership-proof UI + end-user submit-signed UI (needs an in-browser signing helper) | `round4-lane-L7-frontend.md` | first in-browser External-wallet flow |
| LOW findings across reviews and re-checks (RV-L1 R-1..R-3, RV-L5 re-check LOWs, RV-L7 re-check 409 pending notice, RV-L4-04 residuals, etc.) | `.omc/research/round4-review/*.md` (local) + `round4-review-ledger.md` | post-QA LOW sweep |
| Frontend vocabulary ratchet re-recorded (+26 debt) | L7 decision | retired-vocabulary sweep track |
| Railway SurrealDB **generation reset** (D-9) — one-time owner step at first deploy | D-9 | **DONE 2026-10-18 05:07Z** — `REMOVE DATABASE azoa; DEFINE DATABASE azoa` in NS `azoa` only (volume + `main` namespace untouched; 6 old-schema `avatar` rows dropped); schema job applied 149 files, 0 mismatches, runtime user provisioned |
| Merge `feat/platform-stub-closeout` state into `oasis-sleek-38` / vocabulary branch at a quiet point | RUNBOOK §5 | after QA |

## Live QA deploy (2026-10-18)

`main` `d0e9eed1` deployed to Railway `ardanova`/production by commit SHA: azoa-schema `0371938b` SUCCESS, azoa-api `45cbfe7d` SUCCESS, azoa-frontend `0e33d51b` SUCCESS (first frontend build failed: Dockerfile did not build the `file:` SDK dependency — fixed in `frontend/Dockerfile`, verified with a local podman build + run). Smoke: API `/health` 200; bad login → 401 `Invalid credentials.` with CORS headers; frontend `/login` 200.

## Recorded during QA

_Add QA findings here (date · flow · observed · expected · severity), then batch them into one fix round._
