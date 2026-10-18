# Dashboard routes — design notes

## §first-party-money

`verify-identity`, `funds` and `cash-out` are the person's own money surfaces (round 4, plan §4 L7).
They call only first-party routes (`POST /api/kyc/session|refresh`, `GET /api/kyc/status`,
`/api/fiat/checkout/{userId}`, `/api/fiat/payouts/{userId}`); payout-account setup is first-party
only server-side (F-H8), so a tenant session gets a 403 there by design.

- **Keys before egress.** Funds, cash-out and the tenant payout top-up claim their idempotency key
  through `lib/persisted-request-key.ts` (`claimRequestKey`), saved in `localStorage` *before* the
  request leaves and reused for the same amount. A lost response is retried with the same key, so a
  double-click or a reload cannot charge or pay twice.
- **An unresolved key is never replaced silently (RV-L7 L7-4).** If a saved key exists for a different
  amount, `claimRequestKey` returns `pending` and the page shows `PendingRequestNotice`: resume that
  exact request, or explicitly discard it. The key covers only an exact-amount retry; overwriting it
  when the amount is edited is how one lost cash-out became two. Cash-out re-reads its history after
  every attempt, failed ones included, so a claim whose answer was lost is visible before the person
  decides.
- **Keys are resolved by state, not by landing page (L7-3, L7-8).** The key is forgotten on a definite
  answer only: success, a terminal checkout intent (`isFinishedCheckout`: complete/failed/expired, or
  settlement `funds_applied`/`payment_failed`), or a typed refusal (`isDefiniteRefusal`: 400/403/404/422).
  Network failures, 401, 409, 429 and 5xx keep it. Funds stores the intent id beside the key
  (`rememberServerId`) and resolves it on every visit, wherever Stripe returned to; it also passes
  `successUrl`/`cancelUrl` = `/funds` (origin-allowlisted). A same-key resume of an already finished
  intent clears the key and says nothing new was charged, never "the funds are in your account" for a
  new top-up.
- **Balance moves on the webhook, not the redirect.** The funds page shows the intent's
  `settlementState`; only `funds_applied` says the account changed. Returning from the payment page
  also reads the intent from the `?intent=` parameter (read from `window.location`, so the page needs no
  Suspense boundary for `useSearchParams`).
- **Refusals in words.** `lib/refusal-messages.ts` turns an `SdkError` into one actionable sentence;
  `KYB_REQUIRED` (D-6) maps to the business-verification sentence wherever a tenant-driven or
  custodial call is refused.
- **Verify identity** re-reads `/status` right after a session starts so Refresh is offered for the
  new attempt (the slice `src/test/slices/verify-identity.slice.test.tsx` caught the version that
  left it disabled). `src/test/slices/first-party-money.slice.test.tsx` drives the lost-answer,
  refusal and resolve-by-state flows above against the real SDK.

## §tenant-payout-float

`payout-balance` shows the D-8 tenant floats (`GET /api/fiat/tenant-payout-balance`, one per
currency/environment: available / reserved / maturing top-ups) and starts a top-up checkout
(`POST .../top-up`) with a saved request key. A positive `deficitMinorUnits` (charged-back top-up)
is an alert because every tenant-priced cash-out is refused until it is covered. A non-tenant
principal gets 403; the page says the balance belongs to an organization account.

## §operator-boards (admin)

- `admin/parked-runs` reads `POST /api/action/runs/reconcile-sweep` as `{ scanned, results, errors,
  hardStuck }` through `azoa.api.reconcileParkedRuns()` and words it with `lib/operator-honesty.ts`
  `describeSweep`: zero scanned is a warning ("not a confirmation of anything"), any error is an
  error toast; only a non-zero sweep with every run resolved is a success. The sweep scans a bounded
  batch, so the page re-reads the parked board first and passes the count still parked: a clean batch
  with runs left over is a warning ("sweep again"), not a success (RV-L7 L7-11; a server `remaining`
  field would make this exact, see the fix-frontend lane record).
- `admin/fiat-payouts` offers **Record paid** / **Return balance** on `NeedsOperator`/`Reversed`
  rows (`POST /api/admin/fiat-payouts/{id}/resolve`); the node re-reads Stripe and chain evidence and
  answers 409 when it does not support the choice. The toast reports the status the node returned
  (`describePayoutResolution`): `record-paid` can legitimately end at `BankPending` (funded, bank payout
  not landed), which is a warning and never "Recorded as paid" (L7-5). The duplicate "Recover failed
  bank payout" button was removed.
- `admin/fiat-reversals` offers **Resolve from chain** on a `NeedsOperator` row with a
  `pendingMoveKey` (`POST /api/admin/fiat-reversals/{id}/resolve`), and shows that key.
