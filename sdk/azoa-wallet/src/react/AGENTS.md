# `azoa-sdk/react` — unstyled money components (SR-05 P1)

React `>=18.2 <20` is an **optional peer**: only this subpath imports it. Hosts are React 18.3 and React 19
(Next 16 / Next 15), so the code avoids 19-only APIs (`use`, `useActionState`, ref-as-prop,
`<Context>`-as-provider) and the type surface uses only names both `@types/react` majors export. The
SDK's own tests run on React 18.3 (the lower bound). Everything privileged goes through the host's
`AzoaMoneyTransport` (see `src/money/AGENTS.md` §transport); nothing here sees a credential.

## §provider

`<AzoaMoneyProvider transport subject|keyStore messages? slots? onRedirect? onRefusal? allowedRedirectOrigins?
allowLoopbackRedirects?>`. Without a `keyStore`, it builds `createKeyStore({ subject })` over `localStorage`;
with neither it throws during render (fail closed: no shared key slot). Keep `transport` stable
(`useMemo`): the provider's account store is keyed on it, so a new object each render means a fresh store
and a fresh account read.

**A subject change is a new person (M2).** A host BFF's transport is session-agnostic, so a same-tab user
switch often keeps the same `transport` object and changes only `subject` (or `keyStore`). The account
store is therefore keyed on `transport` + key store + `subject`: the previous person's wallet address, KYC
status and live `kycSession.verificationUrl` are dropped, not carried over. Every hook resets its
per-person state (pending request, intent, cash-out history, sellable, balance) when the key store
changes, and bumps an `epoch` ref so an answer to a request sent for the previous person is dropped
(never applied, never redirected).

**Redirect policy (L3).** `allowedRedirectOrigins` (exact origins, e.g. your payment, verification and
payout providers) and `allowLoopbackRedirects` (development only) become the context's `redirectPolicy`;
every `onRedirect` and every rendered link goes through `safeRedirectUrl(url, redirectPolicy)` (see
`src/money/AGENTS.md` §presentation). Omitted, any `https:` origin is admitted.

## §account-store

One `AccountStore` per provider and subject (an external store read through `useSyncExternalStore`).
`MoneyGate`, `KycStep`, `ConsentStatus` and `CustodialAccountCard` all read the same snapshot, so a consent
revoked in one is `revoked` in the gate immediately and the account is read once, not once per component.
`beginKyc` ensures the account first (idempotent), so a host transport need not.

## §hooks

`useCustodialAccount`, `useAddFunds({ returnedIntentId })`, `useCheckoutStatus(intentId)`, `useCashOut`,
`useSellUnits`, `useBalance`. Hooks run the `azoa-sdk/money` flows (`startCheckout`, `sendCheckout`, ...);
they add only React state and events. Every transport call goes through `settleMoney` (or the flows'
own guard), so a host transport that rejects or resolves junk (`{ ok: true, value: null }`) shows an
`unconfirmed` refusal instead of an unhandled rejection. A ref-held `sending` flag stops a same-tick double
click from sending (or redirecting) twice before `busy` renders.

**Host callbacks cannot break state (L4).** `onRedirect`, `onRefusal` and `onSettled` run through
`callHost`, which logs a throw (`console.error`) and carries on. Each `run` separates "send" from "apply":
only a send failure becomes `unconfirmed`, so a throwing `onRedirect` can no longer turn an opened payment
into a fake "not confirmed" notice, and `sending`/`busy` are reset in a `finally`, so no throw (host or
SDK) leaves a form busy.

**Saved requests follow storage (L1).** `resume()` goes through `send*`, which re-reads the saved request
under the lock and answers `superseded` (shown as `pending.superseded`) instead of resending a key another
tab already resolved. Each hook also listens for `storage` events (which fire only in the *other* tabs) and
re-reads its slot, so a resolved request stops being offered and another tab's lost answer is offered here
too. Both are skipped while this hook is sending.

Events: `onRedirect(url, kind)` with `kind` `checkout` | `kyc` | `payout-setup` — **the host navigates; the
SDK never sets `location`.** URLs pass `safeRedirectUrl` first. When no `onRedirect` exists (prop or
provider), components render a plain link instead. A late answer after unmount never redirects.
`onRefusal(refusal)` fires for refusals a person's action produced (not for background reads, which render
state). `onSettled(view)` fires once per outcome the person was waiting on (below).

## §components

`<MoneyGate require="ready">` renders children only when the gate is `ready`, otherwise the step that gets
there (`KycStep` for `setup`, a review notice with `reviewDueAt`, `ConsentStatus` for `revoked`, the reason
for `unavailable`). `<KycStep>`, `<ConsentStatus>` (withdrawing needs a confirm click),
`<CustodialAccountCard>`, `<AddFundsForm currencyCode presets? min? max? returnedIntentId?>` (amounts in minor
units of `currencyCode`, which is required: it decides the decimal places, and an unknown code accepts no
amount), `<CheckoutStatus intentId>`, `<WithdrawForm>`, `<PayoutDestinationStep accountHolderSetupUrl?>`,
`<SellUnitsForm>`, `<BalanceSummary>`, `<PendingRequestNotice kind pending>`. All copy comes from
`messages` (`defaultMoneyMessages`, overridable per section on the provider).

## §add-funds

On mount, in this order: (1) a saved key with no server id is a lost answer → `pending`, offered as "Resume
that payment" **instead of** a new payment form; (2) with `returnedIntentId`, that intent is shown and
polled, and the saved key is resolved by its state, never by which page the provider returned to; (3) a
saved key with another intent id is read: finished → resolved; otherwise offered for resume. `start`
claims a key (persisted first), `resume` re-sends the pending request with its original key, `discard`
forgets it explicitly. On an opened checkout the intent id is saved before `onRedirect` fires.

## §checkout-status

Polls `checkout.get` every `pollIntervalMs` (default 2 s) until **settled** (`isSettledCheckout`), a
key-releasing refusal, or `maxPolls` (default 150; `refresh()` restarts). Every read calls
`resolveCheckoutByState`, which forgets the key as soon as the intent is **terminal** — so `complete` +
`payment_received` (or `complete` + `not_started` after a failed Stripe re-read) releases the key but keeps
polling until `funds_applied` (H2). Announce rules, each covered by a test that goes red when inverted:

1. `onSettled` fires only on a settled read, so a non-final `complete` never consumes the announcement.
2. Only to a device that waited: it saw the intent unsettled, or held the saved key for it. A reload of a
   return URL whose payment was already resolved does not announce "funds added" again, and a same-key
   resume of an old settled payment never does.
3. Once per intent (`announced` ref), so a `refresh()` after settlement is silent.

`<AddFundsForm>` runs this hook itself and keeps the new-payment form disabled while the shown intent is
terminal but unsettled: the key is gone by then, so a second submit would be a real second charge. Read
`view.settlementState`: only `funds_applied` means the balance moved.

## §cash-out

`useCashOut` reads availability, payout destination and history together and **re-reads them after every
attempt**, failed ones included, so a claim whose answer was lost is visible before the person decides to
resume. A same-key replay of a refused cash-out is a refusal (see `src/money/AGENTS.md` §flows), never a
success notice or `onSettled`; an accepted one sets `noticeTone` from `isCashOutOnTrack` (`success` for
Requested/UnitsCollected/BankPending/Paid, `warning` for Returned/Reversed/NeedsOperator or anything
unknown). `beginDestinationSetup` hands `setupUrl` to `onRedirect(url, "payout-setup")`. F-H8: the node
refuses tenant-initiated payout setup, so through a host BFF it answers `account_holder_required`;
`PayoutDestinationStep` renders that as information (not an error) and offers `accountHolderSetupUrl`
when the host has one.

## §sale

`useSellUnits` is inert (`supported: false`, `SellUnitsForm` renders nothing) without `transport.sale`.
The sale key is bound to the quote id (`reference`), so resuming replays the identical quote; a new quote
while one is unresolved is `pending`. Quantities stay base-unit strings (BigInt parse/format).

## §slots

Components are unstyled. `slots={{ Button, Input, Card, Alert }}` (per component, or once on the provider)
replaces the defaults (`<button data-variant>`, `<input>`, `<section aria-labelledby>`, `<div role>`); every
component passes `className` to its root. Hosts on Tailwind 3 and 4 adapt their own components to the slot
props. `Alert` tones `error`/`warning` are `role="alert"`, the others `role="status"`.

## §build

tsup's `banner` would mark **every** subpath client-only (breaking hosts that import `azoa-sdk` in Server
Components), and with `treeshake: true` tsup re-bundles each chunk through rollup, which strips module
directives anyway (so `banner` and `renderChunk` additions are both lost). The `use-client-directive`
plugin in `tsup.config.ts` therefore prepends `"use client";` in `buildEnd`, after files are written, to
`dist/react/index.{js,cjs}` only, and prefixes the source map's `mappings` with `;` so lines still align.
Shared chunks need no directive: the entry marks the client boundary.

Watch-out: hosts that link the SDK with `file:` (the AZOA frontend) resolve `react` from the SDK's own
`node_modules` if they import this subpath from the built `dist/`, giving two Reacts. Dedupe `react` in
the host's bundler before adopting `azoa-sdk/react` that way; published installs are unaffected.
