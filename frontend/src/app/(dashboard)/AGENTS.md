# Dashboard routes — design notes

## §first-party-money

`verify-identity`, `funds` and `cash-out` are the person's own money surfaces (round 4, plan §4 L7).
They call only first-party routes (`POST /api/kyc/session|refresh`, `GET /api/kyc/status`,
`/api/fiat/checkout/{userId}`, `/api/fiat/payouts/{userId}`); payout-account setup is first-party
only server-side (F-H8), so a tenant session gets a 403 there by design.

- **Keys before egress.** Funds and cash-out derive their idempotency key from
  `lib/persisted-request-key.ts`, saved in `localStorage` *before* the request leaves and reused for
  the same amount. A lost response is retried with the same key, so a double-click or a reload cannot
  charge or pay twice. The key is forgotten only on a definite server answer (cash-out accepted; a
  checkout intent read as `funds_applied`/`payment_failed`/`expired`). A different amount is a
  different request.
- **Balance moves on the webhook, not the redirect.** The funds page shows the intent's
  `settlementState`; only `funds_applied` says the account changed. Returning from the payment page
  reads the intent from the `?intent=` parameter (read from `window.location`, so the page needs no
  Suspense boundary for `useSearchParams`).
- **Refusals in words.** `lib/refusal-messages.ts` turns an `SdkError` into one actionable sentence;
  `KYB_REQUIRED` (D-6) maps to the business-verification sentence wherever a tenant-driven or
  custodial call is refused.
- **Verify identity** re-reads `/status` right after a session starts so Refresh is offered for the
  new attempt (the slice `src/test/slices/verify-identity.slice.test.tsx` caught the version that
  left it disabled).

## §tenant-payout-float

`payout-balance` shows the D-8 tenant float (available / reserved / aging top-ups) and starts a
top-up checkout. It is coded against the L3 design record (`Services/Fiat/OffRamp/AGENTS.md`
§tenant-funded) before the routes exist: a 404 renders "not offered on this node yet", never an
empty balance. A negative balance (charged-back top-up) is shown as an alert because every
tenant-priced cash-out is refused until it is covered. Revisit the field names when L3 lands
`GET /api/fiat/tenant-payout-balance`.

## §operator-boards (admin)

- `admin/parked-runs` reads `POST /api/action/runs/reconcile-sweep` as `{ scanned, results, errors,
  hardStuck }` through `azoa.api.reconcileParkedRuns()` and words it with `lib/operator-honesty.ts`
  `describeSweep`: zero scanned is a warning ("not a confirmation of anything"), any error is an
  error toast; only a non-zero sweep with every run resolved is a success.
- `admin/fiat-payouts` offers **Record paid** / **Return balance** on `NeedsOperator`/`Reversed`
  rows (`POST /api/admin/fiat-payouts/{id}/resolve`); the node re-reads Stripe and chain evidence and
  answers 409 when it does not support the choice. The duplicate "Recover failed bank payout" button
  was removed.
- `admin/fiat-reversals` offers **Resolve from chain** on a `NeedsOperator` row with a
  `pendingMoveKey` (`POST /api/admin/fiat-reversals/{id}/resolve`), and shows that key.
