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
| **Decide public self-registration for launch** — QA node has `Registration__AllowPublicSelfServiceInProduction=true` (D-12) | Opened for QA account creation; an open production node lets anonymous strangers create identities | Railway variable reviewed/removed before launch |
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

### Live QA pass 2026-10-18 (Playwright, headless, against `main` `d0e9eed1`)

Summary and evidence: `qa-live-2026-10-18.md`. Accounts: `qa-test-accounts.md`. Screenshots are in the gitignored `.omc/qa/2026-10-18/` (file names below live in that directory).

| # | Date | Flow | Observed | Expected | Severity | Evidence |
|---|---|---|---|---|---|---|
| C1 | 2026-10-18 | Operator console: sign in, sign out, step-up, every operator mutation | Every operator BFF mutation returns 403 `The operator request could not be verified.`, including the correct password with the exact public `Origin` and `x-azoa-operator-request: 1` (checked with curl for POST/DELETE `/api/operator/session` and PUT `/api/operator/fees`). Cause: `requireSameOriginMutation` (`frontend/src/lib/operator-bff.ts:86-98`) compares `Origin` with `new URL(request.url).origin`, and behind Railway's proxy that is the container's internal origin. Result: no operator can sign in through the UI, "End operator session" fails, step-up pages (`/operator/wallet-identities` → `/operator/login?reason=step-up`) cannot be reached, and every console write is dead. Local/CI pass because the origins match there. | Operator signs in at `/operator/login` and operates the console | **CRITICAL** | `02-operator-wrong-password.png`, `02-operator-landing.png`, `03-op-operator-wallet-identities.png` |
| H-A | 2026-10-18 | Account creation policy | Production has closed self-registration (`/api/user/register` → 403, correct), but `POST /api/user/auth/verify` (wallet sign-in) still creates a brand-new self-owned user with no registration check (`Managers/WalletAuthManager.cs:172` → `CreateSelfOwnedWalletUserAsync`, no `SelfRegistrationPolicy` check). All QA accounts were created this way. | Either wallet sign-in honours `SelfRegistrationPolicy`, or the owner explicitly decides that wallet self-sovereign sign-up stays open in Production and NODE-HOST §8.10 says so | **HIGH** (owner decision) | `qa-live-2026-10-18.md` §Accounts |
| H-B | 2026-10-18 | Node onboarding (NODE-HOST §8.6 / §8.10) | No operator-side user-provisioning endpoint exists, and email registration plus OAuth (`/auth/oauth/providers` → `[]`) are closed in Production. So "register the first tenant user" cannot be done by any documented route. Only the H-A wallet path works. | A documented first-tenant bootstrap path for a closed node (operator-created tenant principal, or a time-boxed registration window) | **HIGH** (owner decision) | `01-register-valid.png` |
| H1 | 2026-10-18 | Login with a wrong password | UI shows `POST /api/user/login: network request failed`. The API correctly returns 401 `Invalid credentials.` with CORS. Cause: SDK `request()` (`sdk/azoa-wallet/src/api/client.ts:4377-4382`) treats any non-anonymous 401 as an expired session, runs the token refresh, the refresh throws because there is no session, and `handleFetchError` turns that into NETWORK_ERROR. The login call is not marked anonymous. | "Invalid credentials." | **HIGH** | `06-login-wrong-password.png` |
| H2 | 2026-10-18 | API keys page | Crashes ("This page couldn't load", `e.scopes.map is not a function`) once the user has at least one key, revoked keys included. The API returns `scopes` as a comma-separated string, but the page types it `string[]` (`frontend/src/app/(dashboard)/api-keys/page.tsx:35,402`). Listing and revoking through the UI are both impossible after the first key. Create works and shows the one-time reveal. | List and revoke the user's keys | **HIGH** | `08-api-keys-crash-after-revoke.png` |
| H3 | 2026-10-18 | `/fungible-mint` | Crashes for every user (`Cannot read properties of undefined (reading 'map')`). `GET /api/fungible-creation` returns a bare JSON array (`Controllers/FungibleCreationController.cs:60-62`), but SDK `listFungibleCreations` (`client.ts:2506`) uses the enveloped `request()`, so `.result` is undefined. The panel test mocks the SDK, which is why this was missed. | Page loads with an empty list | **HIGH** | `05-user1-fungible-mint.png` |
| H4 | 2026-10-18 | Profile save (`/users`) | Wallet-created users cannot save any profile change. The UI resends `username: "wallet-solana-…"`, and `UserUpdate` validation rejects it ("letters, numbers, and underscores"). The UI shows only `PUT /api/user/{id} failed with HTTP 400` because the ProblemDetails `errors` are dropped. Since wallet sign-in is the only way to get an account in prod (H-A/H-B), this hits every user. | Profile saves. Wallet handles and the validator agree on one username alphabet, and the UI shows field errors | **HIGH** | `11-user2-profile-save.png` |
| H5 | 2026-10-18 | Own profile read | `GET /api/user/{self}` returns `passwordHash` (a bcrypt hash), and the `/users` "Raw Data" panel can show it. Another user only gets `{id, username, title}` (correct). | Password hashes never leave the server | **HIGH** (security) | `qa-live-2026-10-18.md` §Cross-user |
| M1 | 2026-10-18 | `/admin/*` dashboard pages (fiat-payouts, fiat-reversals, parked-runs, sagas) | No principal can use them. They call `/api/admin/*` with the ordinary user Bearer: `dapp:manager` → 403, anonymous → `/login`. Only the node-operator JWT passes (operator Bearer → 200), and the frontend keeps that only in the operator cookie, never as a user Bearer. `/admin/fiat-reversals` also shows "Money taken back 0.00 USD / Nothing has been taken back that is still open" right next to the 403 error banner, which is a made-up empty state. | Admin views reachable from the operator console (or moved under `/operator`), and a failed fetch never renders zero totals | **MEDIUM** | `05-manager-admin-fiat-reversals.png`, `05-manager-admin-fiat-payouts.png`, `05-manager-admin-sagas.png` |
| M2 | 2026-10-18 | `/operator/chains` | `GET /api/operator/ethereum/recovery/Devnet` → 500 "An unexpected error occurred" (also when called directly with an operator token). The rest of the page renders. | 200 with an empty list, or an honest "Ethereum disabled" refusal | **MEDIUM** | `02-op-operator-chains.png` |
| M3 | 2026-10-18 | Tenant payout-balance top-up | With Stripe completely unconfigured (no Stripe variables on `azoa-api`), `POST /api/fiat/tenant-payout-balance/top-up` → 503 and the UI says "The provider is unavailable at the moment. Try again shortly." It also keeps a "top-up of 25.00 has not been confirmed yet — Resume" record. `/funds` handles the same condition honestly ("Adding funds is not configured on this node", button disabled). | The same honest "not configured" refusal as `/funds`, with no resumable pending record | **MEDIUM** | `11-tenant-payout-topup.png` |
| M4 | 2026-10-18 | Action builder as `dapp:user` | The builder is offered, and Create shows `POST /api/action failed with HTTP 403` (empty body). It works as `dapp:developer`. | Either hide/disable authoring for `dapp:user`, or return and show a reason (role required) | **MEDIUM** | `08-action-builder-created.png` |
| M5 | 2026-10-18 | User directory | `GET /api/user` lists every user's id and username to any signed-in user. Wallet usernames embed the auth-address prefix, so anyone can enumerate users and partially link wallets. | Directory scoped (self, tenant, or admin) | **MEDIUM** | `qa-live-2026-10-18.md` §Cross-user |
| L1 | 2026-10-18 | Register page on a closed node | `/register` and "Don't have an account? Create one" are still offered. Submitting shows `POST /api/user/register: Public self-registration is disabled…` (the method/path prefix appears in user copy on every SDK error banner). | Hide or explain registration when the node is closed, and strip method/path from user-facing messages | LOW | `01-register-valid.png` |
| L2 | 2026-10-18 | Verify identity start | Honest refusal, but it shows host config key names to end users: `KYC_POLICY_UNAVAILABLE: … Missing configuration: Kyc__Identomat__CompanyKey, Kyc__Identomat__ConfigId` | End users see "not available yet". Key names stay in the operator console | LOW | `06-verify-identity-start.png` |
| L3 | 2026-10-18 | Console noise | `/verify-identity` 404 `/api/kyc/status` (never started). `/kyc` and `/business-verification` 403 for non-tenant users. `/node-transparency` 503 `/api/node-transparency/audit/checkpoint`. | Expected-state calls return 200 with a state, not error statuses | LOW | `05-user1-verify-identity.png`, `05-user1-kyc.png` |
| L4 | 2026-10-18 | Sign out | Plain "Sign out" only clears local storage, so the old JWT stays valid server-side (200). "Log out of all devices" revokes correctly (401). Probably by design. | Owner confirms local-only sign-out is intended | LOW | `09-after-logout.png` |
| L5 | 2026-10-18 | Visiting `/login` while signed in | Silently wipes the stored session | Redirect to `/overview`, or keep the session | LOW | n/a |
| L6 | 2026-10-18 | Cross-user existence oracle | User2 `GET /api/action/{dev's private id}` → 404 but `DELETE` → 403. `PUT /api/user/{other}` → 400 rather than 403/404 | One response for every operation on a resource you don't own | LOW | `qa-live-2026-10-18.md` §Cross-user |
| L7 | 2026-10-18 | Network switcher (prod) | Header says "Devnet" and offers "Mainnet", while the overview reports Algorand **testnet** under the devnet profile | Labels match the network actually in use, and prod's selectable set is deliberate | LOW | `05-user1-overview.png` |
| L8 | 2026-10-18 | Action builder UX | A successful create shows no confirmation and My Actions still says "0 actions" until reloaded. There is no validate control in My Actions (`POST /api/action/{id}/validate` → "DAG is valid.") | Confirmation, a list refresh, and a visible validate control | LOW | `09-dappdev-actions.png` |
| L9 | 2026-10-18 | Railway config hygiene | `AdminBootstrap__SeedEmail` / `SeedSecret` are set on `azoa-api`, but NODE-HOST §8.9 says "Do not configure them for a new node" | Remove the legacy pair (owner) | LOW | Railway variable names only |
| L10 | 2026-10-18 | `/cash-out` | An unverified user sees "Identity verification **Available**" | A label that says the state ("Not verified") | LOW | `06-cash-out-setup.png` |

#### Status after QA re-run on `0de1b99d` (2026-10-18)

Evidence: `qa-live-2026-10-18.md` § Re-run (0de1b99d). Screenshots `20-*`, `21-*`, `22-*`, `23-*`.

| # | Status | Re-run evidence |
|---|---|---|
| C1 | **FIXED-VERIFIED** | Real `/operator/login` UI: wrong password → 401 "The username or password was not accepted."; correct → 200, `/operator`, cookie HttpOnly/Strict/Secure at `/api/operator`, nothing in localStorage. Writes go through the BFF: fee schedule publish PUT 200, KYC provider policy save PUT 200 (profile/trust revision 0→1). `/operator/wallet-identities` renders on a fresh session. Step-up: after 11 minutes `/operator/wallet-identities` → `/operator/login?reason=step-up&returnTo=…` ("Please sign in again to confirm the sensitive action you selected."), re-auth returns to the page and it renders. "End operator session" DELETE 200, cookie cleared, protected page → `/operator/login`. (`20-op-*.png`, `21-op-step-up-*.png`) |
| H-A | **FIXED (code), not live-verifiable** | D-11: wallet/OAuth first sign-in now follows `SelfRegistrationPolicy` (`WalletAuthManager`). Registration is open on this node (D-12), so the closed-node refusal can't be watched live. Re-check if the node is ever closed again |
| H-B | **RESOLVED by owner decision D-12** | Email registration open on the QA node. UI registration of `qa.email1+r4@azoa.test` → 200 "Created.", signed in at `/overview` (`22-register-email1.png`) |
| H1 | **FIXED-VERIFIED** | Wrong password for a real email account shows "Invalid credentials." (`22-login-wrong-password.png`) |
| H2 | **FIXED-VERIFIED** | `/api-keys` renders with a revoked key in the list. UI create shows reveal `azoa_438d5caf6a1`, the list shows both keys, UI revoke → `isActive:false` (`22-api-keys-*.png`) |
| H3 | **FIXED-VERIFIED** | `/fungible-mint` renders the harness, no page errors (`22-fungible-mint.png`) |
| H4 | **FIXED-VERIFIED** | Wallet user USER2 saves First/Last name through the UI (PUT 200, persisted). An invalid username shows the field rule ("3-50 characters … letters, numbers, underscores, or hyphens") (`22-profile-invalid-username.png`) |
| H5 | **FIXED-VERIFIED** | No `passwordHash` / `authNotBefore` in register, login, wallet verify, self GET, PUT, or other-user GET responses (email and wallet users) |
| M1 | **FIXED-VERIFIED** | `/operator/payouts`, `/operator/reversals`, `/operator/parked-runs` render honest empty states with no errors. The old `/admin/fiat-*`, `/admin/parked-runs`, `/admin/sagas` now send a signed-in user to `/operator/login` (`20-op-operator-*.png`) |
| M2 | **FIXED-VERIFIED** | `GET /api/operator/ethereum/recovery/Devnet` → 409 `ETHEREUM_RECOVERY_UNAVAILABLE`. The panel says "Ethereum is not enabled on this node, so there are no preparations to recover." (`20-op-operator-chains.png`). The browser console still logs the 409 (cosmetic) |
| M3 | **FIXED-VERIFIED** | Tenant top-up shows "Payments are not configured on this node, so nothing can be charged here yet. Nothing was charged." with no resumable pending record. `/funds` unchanged and honest (`22-tenant-payout-topup.png`, `22-funds.png`) |
| M4 | **FIXED-VERIFIED** | `dapp:user` Create shows "Creating and changing actions needs the DApp developer role. Ask a DApp manager or the node operator to assign it to your account." (`22-action-builder-dapp-user.png`) |
| M5 | **FIXED-VERIFIED** | `GET /api/user` → 403 for `dapp:user` and `dapp:manager`, 200 for the node operator (id/username/title only) |
| L1 | **FIXED-VERIFIED** (partial) | New refusal copy no longer carries the method/path prefix (M3/M4/KYC). The register page is moot now that registration is open |
| L2 | **FIXED-VERIFIED** | KYC start: "Identity verification is not available on this node yet. The node operator still has to finish setting it up." with no config key names (`23-verify-identity-start.png`) |
| L6 | **STILL FAILING** (partial) | `PUT /api/user/{other}` now → 404 (fixed). `DELETE /api/action/{other's private id}` still → 403 while GET → 404 |
| L9 | **STILL OPEN** (owner) | `AdminBootstrap__SeedEmail` / `SeedSecret` are still set on `azoa-api` |
| L10 | **FIXED-VERIFIED** | Label now "What you can cash out" |
| L3, L4, L5, L7, L8 | not re-tested | Unchanged from the first pass |
| NEW-1 | **NEW · LOW** | The dashboard sidebar still shows an "ADMIN" group (Saga Console, Waiting on Settlement, Returned Payments, Cash-outs) to ordinary users, and each entry now bounces to `/operator/login`. Hide it for non-operators |
| NEW-2 | **NEW · LOW** | Fee schedule "Publish" of an unchanged all-zero schedule returns 200 but the page stays at "version 0" with "No recorded changes". Unclear whether that is an intended no-op. Confirm, or say "nothing changed" |
| NEW-3 | **NEW · INFO** | `/operator/chains` WTT stage-key keyring shows DEGRADED ("New stage-key transfers are refused until the keyring is ready"). The refusal is honest. Owner to provision before `Wtt:Enabled` |
