---
type: lane-checkpoint
round: 4
lane: fix-frontend
branch: r4/fix-frontend
base: 21da6183
findings: [L7-1, L7-2, L7-3, L7-4, L7-5, L7-6, L7-7, L7-8, L7-9, L7-10, L7-11, F-M11-ui]
merged: pending
---

# Round 4 fix batch: r4/fix-frontend (SDK + frontend + operator BFF)

Input: `.omc/research/round4-review/RV-L7.md`, plus the orchestrator's scope addition for the F-M11 UI (contract
read from `r4/fix-frontend`'s sibling `r4/fix-fm11` @ `9297d188`). Edits stay in `frontend/**`, `sdk/azoa-wallet/**`
and this record. No controllers, Program.cs or config were touched, so there is no `## Hunks` section. No new
backend routes, so there are no route inventories.

## Finding → fix → test

| id | fix | test (goes red on regression) |
|---|---|---|
| L7-1 HIGH | `frontend/src/lib/operator-routes.ts`: the Ethereum recovery GET is marked `bare: true`, because `EthereumOperatorRecoveryController.List` returns `Ok(IReadOnlyList<…>)`. `frontend/src/lib/operator-bff.ts` `sanitizedOperatorResponse` now refuses (502) any body on a non-bare route whose `isError` is not a boolean, so drift is no longer read as `{result:null}`. SDK `listEthereumPreparations` (user and operator) switched to `requestBare`, because both controllers are bare. Before this fix it resolved `ok(undefined)`, and `swap/page.tsx` called `.find` on it. | `src/test/slices/operator-console.slice.test.tsx` "lists stuck platform preparations…" (real route handler → MSW bare list). `src/lib/__tests__/operator-bff.test.ts` (bare vs envelope table). SDK `client.round4.test.ts` "resolves to the exact server body". |
| L7-2 HIGH | The wallet-identities GET is marked `bare: true` (the controller returns the bare `View`). The page's clear handler now reads `AZOAResult<bool>`, refuses a non-`true` answer, re-reads the view, and reports the re-read status. `useOperatorResource.reload()` now returns the fresh value. SDK `getWalletIdentityMigration` → `requestBare`. `clearWalletIdentityConflict` → `Result<boolean>`. | Console slice "reads the bare wallet-identity view, clears…" (asserts the re-read status and the exact clear body). SDK shape tests. |
| allowlist audit | I grepped every `CONSOLE_ROUTES` entry against its merged controller action. Deployments, stuck ops, retired rows, DEX settle, WTT (all six), Ethereum activate/recover and node fees are `AZOAResult` (enveloped). The sagas routes are bare (already flagged). I also checked the legacy `resolvePath` controllers (NodeOperator, AmlReview, WebhookEvent/Consent/Registration, HostedSession, ActionRecovery, FiatEvent, OnRampLot, PlatformToken/Genesis): all return `AZOAResult`, so the new strict envelope check cannot break them. | The console slice "refuses an unwrapped body on an enveloped route" (the fees page shows "invalid operator response" instead of a blank schedule). |
| L7-3 MED | `frontend/src/lib/persisted-request-key.ts`: `claimRequestKey` (a discriminated `ready`/`pending` result), `rememberServerId`, `isFinishedCheckout`, `isDefiniteRefusal`. Funds stores the intent id beside the key and resolves the key by that intent's state on every visit, whichever page Stripe returned to. It passes `successUrl`/`cancelUrl` = `${origin}/funds`. A same-key resume of a finished intent clears the key and says nothing new was charged. The tenant top-up page gets the same terminal handling. | `src/test/slices/first-party-money.slice.test.tsx` "funds: a payment settled while away is resolved on return…" (a second same-amount payment creates a second intent and is not shown as "(resumed)"). Unit table in `persisted-request-key.test.ts`. |
| L7-4 MED | Cash-out (and funds/top-up) never overwrite an unresolved key. A different amount returns `pending`, and the new `components/shared/pending-request-notice.tsx` offers "Resume that cash-out" or an explicit discard. Cash-out re-reads its history after every attempt, failures included. | Money slice "cash-out: a lost answer shows the claim, and an edited amount resumes it…" (the fake API ends with exactly one claim). |
| L7-5 MED | `lib/operator-honesty.ts` `describePayoutResolution`: Paid/Returned → success; `BankPending` → a warning ("has not landed yet"); any other status → a warning naming it. `admin/fiat-payouts` uses it for record-paid/return-balance, and the other row actions append the returned status. | `operator-honesty.test.ts` payout table. |
| L7-6 MED | `app/operator/(console)/genesis/page.tsx` uses no browser storage. Resume state (operation id plus the unacknowledged command's kind/version/request-or-quote id) lives in the URL via `history.replaceState` and is validated on read. The definition stays in memory. The `resolved-command` audit write was dropped. The test was rewritten to seed and assert the URL and contains no storage words. | `genesis/page.test.tsx` (3 tests: reconcile from the URL, identical replay after a lost answer, malformed URL command ignored). `NodeOperatorSecurityContractTests`: see Verification below. |
| L7-7 MED | Slices now exercise real response shapes. `operator-console.slice.test.tsx` routes the console fetch through the real BFF handler to MSW upstream bodies (Ethereum list, wallet identities, sagas, plus one malformed body). New money and NFT-stage-cost slices run against the real SDK. | Mutation check done: with the pre-fix `operator-routes.ts` + `operator-bff.ts` restored, 3 of the 4 console slices fail. |
| L7-8 LOW | `isDefiniteRefusal` (400/403/404/422) clears the key. Network errors, 401, 409, 429 and 5xx keep it. | Money slice "a definite refusal releases the key…". Unit table. |
| L7-9 LOW | The sagas page asks for the server maximum (500), says how many action-workflow steps it hid (with a link to Actions), and warns on a full page. The empty state no longer claims "nothing waiting" when the page was full. | Console slice "shows generic saga steps… says how many workflow steps it hid". |
| L7-10 LOW | `azoa-sdk` 0.4.0 → **0.5.0** (0.x: breaking ⇒ minor). Covers the removal of the `./dex` export, the Tinyman peer and `wallet.getSwapQuote/buildSwap`, plus the `clearWalletIdentityConflict` type. New `sdk/azoa-wallet/CHANGELOG.md`. Both lockfiles were bumped in step (frontend `../sdk/azoa-wallet` entry). | `npm ci` in a fresh worktree. |
| L7-11 LOW | `describeSweep(sweep, parkedAfter)`: the parked-runs page re-reads the board before choosing a verdict. A clean batch with runs still parked is a warning ("sweep again"), not success. | `operator-honesty.test.ts` sweep rows. |
| F-M11 UI (scope add) | SDK: `quoteNftStageCosts`/`acceptNftStageCosts` (`/api/nft-stage-costs/{id}/quote|accept`), the `NftStageCostAcceptance` type, view fields `needsStageCosts`/`stageCostPayerUserId`/`acceptedStageCostLimits`, and `isNftStageCostsChanged` + `NFT_STAGE_COSTS_CHANGED`. UI: `components/shared/nft-stage-costs.tsx` gives the payer a quote → review → accept flow, and the acceptance is saved before it is sent so a retry reuses the same stage request id. `nft-lifecycle-panel.tsx` shows the step to the payer only and a one-line note to others. It adds "Continue with accepted costs". It keeps an `NFT_STAGE_COSTS_CHANGED` refusal as `stageCostsChanged`, because the view cannot show it, and the step then offers "Review current stage costs". | `src/test/slices/nft-stage-costs.slice.test.tsx`: payer accepts, costs rise, continue is refused, re-quote, re-accept, stage reaches Prepared with 2 distinct acceptances. A non-payer is told who must act and cannot quote. SDK route/body/refusal-code tests. |

## Verification (fresh, this worktree)

- SDK: `npm run build` ✓; `npx vitest run` → 31 files / 305 tests passed.
- Frontend: `npm run typecheck` ✓; `npm run lint` → 0 errors (118 warnings repo-wide; the new ones are the
  codebase's existing `react-hooks/set-state-in-effect` pattern in the money pages and the stage-cost step);
  `npm test` (unit + slices) → 47 files / 248 tests passed; `npm run build` ✓.
- .NET: `dotnet test tests/AZOA.WebAPI.Tests --filter FullyQualifiedName~NodeOperatorSecurityContractTests` →
  Passed 6/6 (run by filter, as the brief allows for an already-failing owned test).

## Tests cut or declined

- None deleted. The previous genesis test was rewritten, not dropped, because it asserted on browser storage.
- I declined a separate unit test for `PendingRequestNotice`. It is presentational, and both money slices cover it.

## Needs other lane (backend; do not edit controllers here)

- `Controllers/EthereumOperatorRecoveryController.cs:22`, `Controllers/EthereumRecoveryController.cs:24`,
  `Controllers/WalletIdentityOperatorController.cs:19` return bare bodies. The client now pins that contract, with
  `bare: true` in `frontend/src/lib/operator-routes.ts` and `requestBare` in the SDK. If a backend lane wraps them in
  `AZOAResult`, it must flip both in the same change, or the console and SDK will read the envelope as the payload.
- `Managers/ActionManager.cs` sweep result (L7-11): add `remaining` (parked minus scanned). The page can then
  replace its re-read count with an exact figure.
- `Controllers/SagaOperatorController.cs:44` (L7-9): an optional saga-name exclude filter would let the console
  page past action-workflow rows server-side.
