---
type: lane-checkpoint
round: 4
lane: fix-frontend2
branch: r4/fix-frontend2
base: feat/platform-stub-closeout@75b23986
findings: [browser-ci-regression, fix-money-L7, fix-security-L7]
merged: pending
---

# Round 4 fix batch: r4/fix-frontend2 (CI regression + money/security SDK follow-ups)

Edits stay in `frontend/**` and `sdk/azoa-wallet/**`. No controllers, `Program.cs` or config were touched, so
there is no `## Hunks` section.

## DONE

### 1. Browser CI regression (`npm run test:browser`)

Both failures were real, not flaky:

- **`operator-console.slice.test.tsx`** imports the real Next.js route handler
  (`app/api/operator/[...path]/route`) to exercise the BFF layer end-to-end (by design, see
  `src/test/AGENTS.md` §slices). That pulls in `next/server`, whose bundled `ua-parser-js` reads
  `__dirname` — a Node global a real Chromium page does not have — so the whole file failed to
  import under `slices-browser`. Fix: scoped it out of the `slices-browser` Vitest project via a new
  `browserIncompatibleSliceTests` exclude list in `frontend/vitest.config.ts`, with a comment
  explaining why; it still runs (and passes) under the jsdom `slices` project, so `npm test` coverage
  is unchanged.
- **`verify-identity.slice.test.tsx`** (2 tests) — genuine latent race in
  `src/app/(dashboard)/verify-identity/page.tsx`'s `begin()`: it called `setSession(started.value)`
  immediately after the first await, then awaited a second request before `setStatus(...)` and the
  `finally`'s `setBusy(false)`. That put a render on the wire where the "Refresh result" button's
  `session` was already truthy but `busy` was still `true` (so the button is disabled) — invisible
  under jsdom (MSW resolves fast enough that both renders collapse into one before Testing Library's
  first poll) but real under Chromium's real `fetch`/Service Worker round trip. Fixed by committing
  `setSession`/`setStatus`/`setBusy(false)` together after both awaits resolve, so there is no
  intermediate render where the button is clickable-looking but disabled. No assertions were weakened
  or deleted.

Verified: `npm run test:browser` 7 files / 18 tests passed (was 2 files failing). `npm test` 48 files /
251 tests passed (250 baseline + 1 new test, see below).

### 2. Money fixer follow-ups (`round4-lane-fix-money.md` §Needs other lane)

- **SDK**: `TenantPayoutFloatView` (`sdk/azoa-wallet/src/api/money.ts`) gained `strandedReservedCount: number`
  and `oldestReservedAt: string | null`, mirroring `TenantPayoutFunding.cs:36-52`.
- **SDK**: new `PayoutAccountSetupResolution` type + `AzoaApiClient.resolvePayoutAccountSetup(userId, reason)`
  (`sdk/azoa-wallet/src/api/client.ts`), `POST /api/admin/fiat-payouts/accounts/{userId}/setup/resolve`
  (`FIAT_PAYOUT_ACCOUNT_SETUP_RESOLVE` in `api-version.ts`), matching `FiatPayoutOperatorController.cs:114-131`
  / `PayoutAccountService.cs:343,358` (`outcome: "attached" | "released"`).
- **SDK**: new `isRecentOperatorLoginRequired(error)` + `RECENT_OPERATOR_LOGIN_REQUIRED` constant in
  `sdk/azoa-wallet/src/core/errors.ts` (same shape as `isKybRequired`/`isNftStageCostsChanged`), exported from
  `core/index.ts` and the package root `index.ts`.
- **Frontend — step-up prompt**: new shared `frontend/src/components/shared/step-up-banner.tsx`
  (`/login?reason=step-up&returnTo=<path>`, same wording/shape as the existing `(dashboard)/kyc/page.tsx`
  banner). Wired into `admin/fiat-payouts/page.tsx` and `admin/fiat-reversals/page.tsx`: every mutating
  action (`resolve`, `act`) now routes its error through a `reportActionError` helper that checks
  `isRecentOperatorLoginRequired` and shows the banner instead of a bare toast.
  **`exchange/page.tsx`'s retired-row resolve needed no change** — it already proxies through
  `/api/operator/...` (`operator-routes.ts`), and `operator-bff.ts`'s `sanitizedOperatorResponse` already
  turns the backend's `X-AZOA-Auth-Requirement: recent-operator-login` header into the same
  `RECENT_OPERATOR_LOGIN_REQUIRED` code, which `operator-client.ts` already turns into a redirect to
  `/operator/login?reason=step-up`. Traced this end-to-end before deciding not to touch it.
- **Frontend — setup/resolve UI**: new "Stuck account setup" card on `admin/fiat-payouts/page.tsx` (user-id
  input + reason prompt + `resolvePayoutAccountSetup`), toasting the honest outcome (`attached` with the
  connected account id, or `released` with none).
- **Frontend — stranded float UI**: `payout-balance/page.tsx` now shows a `role="alert"` banner with the
  stranded count and oldest-reservation date when `!float.consistent`.
- New test: `payout-balance.slice.test.tsx` gained a `stranded` fixture + a test asserting the new banner
  (both the count and a date-shaped string, deliberately not pinned to an exact calendar day — a first
  attempt pinning `10/1/2026` was timezone-flaky under the Chromium project's local TZ and was reworded to
  a regex before landing).

### 3. Security fixer follow-up (`round4-lane-fix-security.md`, RV-L4-03)

- `WalletOwnershipChallenge`/`WalletOwnershipProof` doc-comments (`sdk/azoa-wallet/src/api/identity.ts`)
  updated to name the purpose-bound `AZOA-WALLET-OWNERSHIP-v1` contract and mutual exclusivity with login
  challenges.
- `AzoaApiClient.proveWalletOwnership` (`client.ts`) — `message` changed from optional to **required**
  (`assertNonEmpty`), so every SDK caller echoes the challenge's exact text rather than falling back to
  the server's "latest live challenge for this address" resolution (up to 5 can be live at once, so that
  fallback is ambiguous under concurrency even though it still works). No call sites existed in `frontend/`
  or `sdk/` outside the method definition (grep-clean), so this is a non-breaking tightening, not a migration.
  `connect` already refuses `signedMessage` on the backend and neither the SDK nor frontend ever sent it
  (grep-clean, confirmed again) — no SDK/UI change needed there.

## Verification (fresh run, this worktree)

- SDK: `npx tsc --noEmit` clean; `npx vitest run` 31 files / 305 tests passed; `npm run build` (tsup) succeeded.
- Frontend: `npm run typecheck` clean; `npm run lint` 0 errors (118 pre-existing warnings, none in touched
  files); `npm test` 48 files / 251 tests passed; `npm run test:browser` 7 files / 18 tests passed; `npm run
  build` (Next.js, Turbopack) succeeded, all routes compiled including `/admin/fiat-payouts`,
  `/admin/fiat-reversals`, `/payout-balance`, `/verify-identity`.
- Generated artifacts cleaned before handoff: `frontend/next-env.d.ts` and `frontend/package-lock.json`
  reverted to their committed state (the `npm install` needed to stand up this fresh worktree touched both
  incidentally — the lockfile diff was an unrelated pre-existing `@tinymanorg/tinyman-js-sdk` optional-peer
  drift, not anything this lane changed), `frontend/src/test/msw/public/mockServiceWorker.js` reverted,
  `.vitest-attachments/` and `src/test/slices/__screenshots__/` removed. `frontend/.next/`,
  `frontend/tsconfig.tsbuildinfo` and `sdk/azoa-wallet/dist/` are gitignored build output, left in place.

## NOT DONE / deferred

- **Exchange retired-row resolve UI**: confirmed already correctly wired (see above) — no gap, not deferred,
  just worth flagging as "investigated, no change needed" rather than silently skipped.
- **No backend/.cs changes**: out of scope per the brief (stay in `frontend/**` + `sdk/azoa-wallet/**`); the
  money-fixer and security-fixer lanes already shipped the backend side on their own branches.
- **No dedicated SDK test for `resolvePayoutAccountSetup` / `isRecentOperatorLoginRequired` / the tightened
  `proveWalletOwnership` signature**: the SDK's existing `tests/api/client.round4.test.ts`-style parity
  suites assert path/method shape for sibling methods via MSW-free fetch mocks; adding one for each new
  method would mechanically repeat that pattern. Deferred rather than skipped silently — flagged here so a
  follow-up lane (or the reviewer) can decide whether to add them before merge; the frontend slice coverage
  (step-up banner, stranded float) exercises the SDK methods indirectly through the real client.
- **No per-row "resolve stuck setup" affordance**: `FiatPayoutView` doesn't carry a clean signal
  distinguishing "this row's account setup is stuck" from other failure modes, so the new control is a
  standalone user-id input on the fiat-payouts page rather than a button on a specific row. If the
  money-fixer lane later adds such a signal to `FiatPayoutView`, this is the natural place to upgrade it to
  a per-row action.

## Changed files

- `frontend/src/app/(dashboard)/admin/fiat-payouts/page.tsx`
- `frontend/src/app/(dashboard)/admin/fiat-reversals/page.tsx`
- `frontend/src/app/(dashboard)/payout-balance/page.tsx`
- `frontend/src/app/(dashboard)/verify-identity/page.tsx`
- `frontend/src/components/shared/step-up-banner.tsx` (new)
- `frontend/src/test/slices/payout-balance.slice.test.tsx`
- `frontend/vitest.config.ts`
- `sdk/azoa-wallet/src/api/api-version.ts`
- `sdk/azoa-wallet/src/api/client.ts`
- `sdk/azoa-wallet/src/api/identity.ts`
- `sdk/azoa-wallet/src/api/money.ts`
- `sdk/azoa-wallet/src/core/errors.ts`
- `sdk/azoa-wallet/src/core/index.ts`
- `sdk/azoa-wallet/src/index.ts`
