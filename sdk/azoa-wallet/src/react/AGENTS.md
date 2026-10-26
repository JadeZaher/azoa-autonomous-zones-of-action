# `azoa-sdk/react` — unstyled money components (SR-05 P1)

React `>=18.2 <20` is an **optional peer**: only this subpath imports it. Hosts are React 18.3 and React 19
(Next 16 / Next 15), so the code avoids 19-only APIs (`use`, `useActionState`, ref-as-prop,
`<Context>`-as-provider) and the type surface uses only names both `@types/react` majors export. The
SDK's own tests run on React 18.3 (the lower bound). Everything privileged goes through the host's
`AzoaMoneyTransport` (see `src/money/AGENTS.md` §transport); nothing here sees a credential.

## §provider

`<AzoaMoneyProvider transport subject|keyStore messages? slots? onRedirect? onRefusal?>`. Without a
`keyStore`, it builds `createKeyStore({ subject })` over `localStorage`; with neither it throws during
render (fail closed: no shared key slot). Keep `transport` stable (`useMemo`): the provider's account store
is keyed on it, so a new object each render means a fresh store and a fresh account read.

## §account-store

One `AccountStore` per provider (an external store read through `useSyncExternalStore`). `MoneyGate`,
`KycStep`, `ConsentStatus` and `CustodialAccountCard` all read the same snapshot, so a consent revoked in
one is `revoked` in the gate immediately and the account is read once, not once per component.
`beginKyc` ensures the account first (idempotent), so a host transport need not.

## §hooks

`useCustodialAccount`, `useAddFunds({ returnedIntentId })`, `useCheckoutStatus(intentId)`, `useCashOut`,
`useSellUnits`, `useBalance`. Hooks run the `azoa-sdk/money` flows (`startCheckout`, `sendCheckout`, ...);
they add only React state and events. Every transport call goes through `settleMoney`, so a host transport
that rejects shows an `unconfirmed` refusal instead of an unhandled rejection. A ref-held `sending` flag
stops a double click from sending (or redirecting) twice before `busy` renders.

Events: `onRedirect(url, kind)` with `kind` `checkout` | `kyc` | `payout-setup` — **the host navigates; the
SDK never sets `location`.** URLs pass `safeRedirectUrl` first. When no `onRedirect` exists (prop or
provider), components render a plain link instead. A late answer after unmount never redirects.
`onRefusal(refusal)` fires for refusals a person's action produced (not for background reads, which render
state). `onSettled(view)` fires once per outcome the person was waiting on (below).

## §components

`<MoneyGate require="ready">` renders children only when the gate is `ready`, otherwise the step that gets
there (`KycStep` for `setup`, a review notice with `reviewDueAt`, `ConsentStatus` for `revoked`, the reason
for `unavailable`). `<KycStep>`, `<ConsentStatus>` (withdrawing needs a confirm click),
`<CustodialAccountCard>`, `<AddFundsForm presets min max currencyCode returnedIntentId>` (amounts in minor
units), `<CheckoutStatus intentId>`, `<WithdrawForm>`, `<PayoutDestinationStep accountHolderSetupUrl?>`,
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

Polls `checkout.get` every `pollIntervalMs` (default 2 s) until finished, a key-releasing refusal, or
`maxPolls` (default 150; `refresh()` restarts). On a finished read it calls `resolveCheckoutByState`.
`onSettled` fires once per intent, and only when this device was waiting for it: it saw the intent
unfinished, or held the saved key for it. So a reload of a return URL whose payment was already resolved
does not announce "funds added" again, and a same-key resume of an old finished payment never does.
Read `view.settlementState`: only `funds_applied` means the balance moved.

## §cash-out

`useCashOut` reads availability, payout destination and history together and **re-reads them after every
attempt**, failed ones included, so a claim whose answer was lost is visible before the person decides to
resume. `beginDestinationSetup` hands `setupUrl` to `onRedirect(url, "payout-setup")`. F-H8: the node
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
