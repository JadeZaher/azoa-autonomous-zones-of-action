---
type: lane-checkpoint
round: 4
lane: qa-web
branch: r4/qa-web
base: feat/platform-stub-closeout (14bc9484, merged up to cb5e14a8 incl. r4/qa-api)
scope: frontend/**, sdk/azoa-wallet/**
---

# Round 4 · post-live-QA fixer r4/qa-web

Inputs: `qa-live-2026-10-18.md`, `round4-post-qa-deferrals.md` §Recorded during QA, and the r4/qa-api
"Needs other lane" list (`round4-lane-qa-api.md`). Closing vocabulary per the ledger: everything below is
**Built** (compiled + covered by unit/slice tests run in this lane). Nothing was redeployed.

## Finding → fix → test

| # | Fix | Test |
|---|---|---|
| **C1** operator BFF refused every mutation behind Railway's proxy | `frontend/src/lib/operator-bff.ts` `expectedPublicOrigin`: `PUBLIC_ORIGIN` (exact, malformed ⇒ refuse all) else single-valued `X-Forwarded-Proto` + `X-Forwarded-Host`/`Host`; comma chains, paths, credentials, non-HTTP(S) refuse. Documented in `frontend/README.md` and `src/app/operator/AGENTS.md` §Same-origin check behind a proxy | `src/lib/__tests__/operator-bff.test.ts`: direct, proxied (forwarded host / public Host), spoofed Origin, scheme downgrade, ambiguous chain, host-with-path, internal origin, missing Origin, missing intent header, `PUBLIC_ORIGIN` pin, malformed `PUBLIC_ORIGIN` |
| **H1** wrong password ⇒ "network request failed" | SDK `login`/`register` sent anonymously; credential-exchange paths (login, register, auth/challenge, auth/verify, claim) never run `onTokenRefresh` in `request`/`requestBare`. `SdkError.serverMessage` + `userFacingMessage()`; auth context uses it | SDK `tests/api/client.qa-live.test.ts` (401 ⇒ `Invalid credentials.`, no refresh, no bearer; stale token not attached; ordinary 401 still refreshes once). Slice `account-surfaces` H1 drives the real login page |
| **H2** API keys page crashed on CSV `scopes` (and read `prefix`/`createdAt`/`isRevoked`, none of which exist) | SDK `normalizeApiKeyScopes`; `ApiKeyInfo`/`ApiKeyCreateResult.scopes: string[]` (CHANGELOG *Unreleased · Breaking*); `createApiKey` accepts list or CSV. Page rewritten onto the typed SDK methods and real field names (`keyPrefix`, `createdDate`, `isActive`/`revokedAt`) | SDK list/create/normalize table; slice H2 lists a CSV-scoped key and a `null`-scoped legacy key, then revokes |
| **H3** `/fungible-mint` crashed | `listFungibleCreations` reads the bare array (`requestBare`), tolerates an envelope, refuses any non-list | SDK bare list / empty list / non-list refused; slice H3 mounts the real panel against a bare-array fake |
| **H4** (UI) profile save showed bare HTTP 400 | SDK `parseErrorBody` flattens `ValidationProblemDetails.errors`; `/users` sends only edited fields (`UserUpdateModel` null = unchanged) and derives the form from the server profile + edits | SDK ProblemDetails test; slice H4: title-only save sends `{ title }`, invalid name shows the pattern message, `wallet-` prefix shows the reserved-prefix message |
| **M1** `/admin/*` unusable + "0.00 USD" beside a 403 | Cash-outs, returned payments, parked runs moved to `/operator/{payouts,reversals,parked-runs}` (git mv) and call the BFF via `lib/operator-result.ts`; `/admin/{fiat-payouts,fiat-reversals,parked-runs,sagas}` redirect (admin sagas duplicated `/operator/sagas`, removed). 9 allowlist entries in `lib/operator-routes.ts` (ids pinned hex32/hex64, bodies rebuilt). Totals and empty-state only render from a successful read. Sidebar + operator shell links updated; unused `StepUpBanner` (linked to the account `/login`) deleted. `admin/key-rotation` stays and says it needs the operator session | `operator-routes.test.ts` rows (7 admitted, 2 refused paths, 2 refused bodies); operator-console slice: board read reaches upstream with the operator cookie Bearer; refused read shows no totals / no `0.00` / no empty sentence |
| **M3** top-up said "try again shortly" with no Stripe | Uses qa-api's `503 PAYMENTS_NOT_CONFIGURED`: honest copy, no resumable record, button disabled (`payout-balance`); `funds` checkout also clears its key on it. `RAIL_UNAVAILABLE` remains transient | `refusal-messages.test.ts`; slice payout-balance M3 |
| **M4** action builder bare 403 for `dapp:user` | `authoringRefusal`: 403 on create/template/publish etc. names the DApp developer role | covered by type/lint; no slice (role is server policy; copy-only change) |
| M2 (UI, qa-api pickup) | BFF forwards allowlisted `ETHEREUM_RECOVERY_UNAVAILABLE` with fixed copy; `useOperatorResource.errorCode`; Ethereum panel shows a neutral state and hides signer controls; no "nothing to recover" beside an error | operator-console slice M2 |
| D-11 pickups | OAuth callback copy for `error=registration_closed`; `403 SELF_REGISTRATION_DISABLED` keeps code + node message | SDK test (verify 403) |
| M5 / H5 pickups | `getAllUsers` documented operator-only; `UserResponse` gains optional `isVerified`, `dappRole`, `createdDate` (no credential fields). No page lists users | n/a |
| L2 | `refusalMessage` strips `Missing configuration: …` and maps `KYC_POLICY_UNAVAILABLE` to plain copy | `refusal-messages.test.ts` |
| L8 | My Actions loads on mount; builder shows "Action created as a private draft" | n/a |
| L10 | Cash-out card retitled "What you can cash out" (the "Available" was the next card's title) | n/a |
| L1 (part) | method/path prefix stripped wherever `userFacingMessage` is used | n/a |

Tests cut/declined: no snapshot or mock-call tests; the existing `fungible-creation-panel.test.tsx` (SDK mocked, which hid H3) was left as is and the real-SDK slice added beside it. M4 got no slice (the copy depends only on status 403).

## Gates (run once at the end, after merging `feat/platform-stub-closeout` incl. r4/qa-api)
- SDK: `npm run build` ✓, `tsc --noEmit` ✓, `vitest run` 32 files / **319 passed**.
- Frontend: `npm run typecheck` ✓ · `npm run lint` **0 errors, 117 warnings** (baseline on main 118; the removed admin sagas page took one) · `npm test` 50 files / **287 passed** · `npm run test:browser` 8 files / **23 passed** · `npm run build` ✓.
- No dependency changes (no `npm audit` delta). `.next` removed after the build.

## Owner / deploy actions
- Set **`PUBLIC_ORIGIN=https://azoa-frontend-production.up.railway.app`** on `azoa-frontend` (Railway). Without it the
  check relies on Railway's forwarded headers, which should work but is unverified live.
- Re-run the C1/M1 QA flows after redeploy: operator sign-in, sign-out, step-up (`/operator/wallet-identities`), a fee
  publish, and `/operator/payouts` + `/operator/reversals`.

## Deferred
- L5 (`/login` while signed in wipes the session): cause not found in the login page or auth context; needs a live repro.
- L1 (hide registration on a closed node): needs a public "registration open?" signal (D-12 opens it on the QA node).
- L3/L4/L6/L7/L9: API status semantics, sign-out policy, network labels, Railway variables — owner/API items.
- Key rotation through the operator console: the BFF does not proxy key material and its 8 s upstream timeout would
  misreport a long rotation; needs a dedicated long-running operator flow.
- Gate authoring UI on `dappRole` (now on `UserResponse`) instead of explaining the 403 after the fact.

## Needs other lane
- `deploy/railway/DEPLOY.md`: add `PUBLIC_ORIGIN` to the frontend variable list (docs only; outside this lane's globs).
