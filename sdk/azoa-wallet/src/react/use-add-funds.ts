// Add-funds state machine: saved key first, resume before new, resolve by state. See `src/react/AGENTS.md` §add-funds.
import { useCallback, useEffect, useRef, useState } from "react";
import {
  MONEY_SCOPES,
  discardCheckout,
  isSettledCheckout,
  moneyRefusal,
  readSavedRequest,
  resolveCheckoutByState,
  sendCheckout,
  startCheckout,
} from "../money/index.js";
import type { CheckoutAttempt, CheckoutCapabilitiesView, CheckoutDiscardAttempt, CheckoutView, MoneyRefusal, RefusedAttempt, SavedRequest } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, redirectTo, settleMoney, useMoneyEvents, useMounted, usePerson, useStorageChange } from "./internal.js";

export interface AddFundsOptions extends MoneyEventProps {
  /** The intent id the payment page returned with (e.g. `?intent=`); resolved by its state before anything new is offered. */
  returnedIntentId?: string | null;
}

export interface AddFundsState {
  capabilities: CheckoutCapabilitiesView | null;
  /** An unresolved saved payment: offer `resume()` (or `discard()`) before any new payment; `needsReview` means only "check again". */
  pending: SavedRequest | null;
  /** The intent to show and poll; while it has no settled read, no new payment is offered. */
  intentId: string | null;
  /** The intent as `begin` answered it, before the first poll. */
  initialView: CheckoutView | null;
  refusal: MoneyRefusal | null;
  /** A neutral sentence (e.g. "nothing new was charged"). */
  notice: string | null;
  busy: boolean;
  canRedirect: boolean;
  /** Claims a key for `amountMinorUnits` (persisted first), then begins the payment. */
  start(amountMinorUnits: number): Promise<void>;
  /** Re-sends the pending payment with its original key, unless another tab already changed it. */
  resume(): Promise<void>;
  /** Asks the node to void the pending payment (never one held for review); its key goes only when the node voids it. */
  discard(): Promise<void>;
}

export function useAddFunds(options: AddFundsOptions = {}): AddFundsState {
  const { transport, keyStore, messages, events: providerEvents, redirectPolicy, identity } = useMoneyContext();
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  const returnedIntentId = options.returnedIntentId ?? null;
  const [capabilities, setCapabilities] = useState<CheckoutCapabilitiesView | null>(null);
  const [pending, setPending] = useState<SavedRequest | null>(null);
  const [intentId, setIntentId] = useState<string | null>(returnedIntentId);
  const [initialView, setInitialView] = useState<CheckoutView | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // The person whose send is in flight: a same-tick double click is dropped, another person's send is not this one's.
  const sending = useRef<object | null>(null);
  // A new person starts from nothing, from their first paint; answers sent for the previous one are dropped.
  const person = usePerson(() => {
    setCapabilities(null);
    setPending(null);
    setIntentId(returnedIntentId);
    setInitialView(null);
    setRefusal(null);
    setNotice(null);
    setBusy(false);
  });

  useEffect(() => {
    let cancelled = false;
    setPending(null);
    setInitialView(null);
    setRefusal(null);
    setNotice(null);
    setIntentId(returnedIntentId);
    void settleMoney(transport.checkout.capabilities()).then(result => {
      if (cancelled) return;
      if (result.ok) setCapabilities(result.value);
      else setRefusal(result.refusal);
    });
    const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (saved && !saved.serverId) {
      // The answer to this request was lost (or the node holds it for review): it is offered before any new payment.
      setPending(saved);
    } else if (saved?.serverId && saved.serverId !== returnedIntentId) {
      if (!returnedIntentId) setIntentId(saved.serverId);
      void settleMoney(transport.checkout.get(saved.serverId)).then(read => {
        if (cancelled) return;
        // Only a settled read lets the key go; an unsettled or unreadable intent keeps it, offered for resume.
        if (!(read.ok && resolveCheckoutByState(keyStore, read.value))) setPending(saved);
      });
    }
    return () => {
      cancelled = true;
    };
  }, [transport, keyStore, returnedIntentId, identity]);

  // Another tab resolved, replaced or lost the saved payment: follow storage instead of offering a stale resume.
  useStorageChange(() => {
    if (sending.current === person.current) return;
    const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (saved && !saved.serverId) setPending(saved);
    else if (pending && pending.requestKey !== saved?.requestKey) setPending(null);
    else if (pending && !pending.serverId && saved?.serverId) {
      setPending(null);
      setIntentId(saved.serverId);
    }
  });

  const apply = useCallback((attempt: CheckoutAttempt) => {
    switch (attempt.kind) {
      case "pending":
        setPending(attempt.pending);
        return;
      case "superseded":
        setPending(attempt.current && !attempt.current.serverId ? attempt.current : null);
        if (attempt.current?.serverId) setIntentId(attempt.current.serverId);
        setNotice(attempt.current ? messages.pending.replaced : messages.pending.superseded);
        return;
      case "opened":
        setPending(null);
        setInitialView(attempt.view);
        setIntentId(attempt.view.intentId);
        // The intent id is already saved, so leaving the page here is safe.
        redirectTo(events, mounted, attempt.paymentUrl, "checkout", redirectPolicy);
        return;
      case "finished":
        setPending(null);
        setInitialView(attempt.view);
        setIntentId(attempt.view.intentId);
        if (attempt.view.resumed) setNotice(messages.addFunds.resumedFinished);
        return;
      case "refused":
        // A kept request replaces the form with its notice, so no second payment is offered meanwhile.
        setPending(attempt.kept);
        // A request held for review is explained by its own notice.
        if (!attempt.kept?.needsReview) setRefusal(attempt.refusal);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
        return;
    }
  }, [events, mounted, messages, redirectPolicy]);

  // Discarding is the node's decision (void-or-reveal); see `src/react/AGENTS.md` §add-funds.
  const applyDiscard = useCallback((attempt: CheckoutDiscardAttempt) => {
    switch (attempt.kind) {
      case "superseded":
        apply(attempt);
        return;
      case "discarded": {
        const { request, intentId: voidedIntentId } = attempt;
        setPending(null);
        // Voided: that intent can never be paid, so it no longer holds the form.
        setIntentId(watched => (watched !== null && (watched === voidedIntentId || watched === request.serverId) ? null : watched));
        setInitialView(null);
        setNotice(messages.pending.voided);
        return;
      }
      case "revealed":
        setPending(null);
        setInitialView(attempt.view);
        setIntentId(attempt.view.intentId);
        // A settled view speaks for itself (funds in, or nothing charged); an unsettled one may still take money.
        setNotice(isSettledCheckout(attempt.view) ? null : messages.pending.revealed);
        return;
      case "refused":
        setPending(attempt.kept);
        // A request held for review is explained by its own notice.
        if (!attempt.kept?.needsReview) setNotice(messages.pending.discardUnconfirmed);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
        return;
    }
  }, [apply, events, messages]);

  const run = useCallback(async <Attempt>(send: () => Promise<Attempt>, applyAttempt: (attempt: NoInfer<Attempt> | RefusedAttempt) => void) => {
    // A double click must not send twice (or redirect twice) before the busy state renders.
    const sentFor = person.current;
    if (sending.current === sentFor) return;
    sending.current = sentFor;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      let attempt: Attempt | RefusedAttempt;
      try {
        attempt = await send();
      } catch {
        attempt = { kind: "refused", refusal: moneyRefusal("unconfirmed"), kept: readSavedRequest(keyStore, MONEY_SCOPES.checkout) };
      }
      // Host callbacks run through `callHost`, so a throwing `onRedirect` can never turn into a fake "unconfirmed".
      if (person.current === sentFor) applyAttempt(attempt);
    } finally {
      if (sending.current === sentFor) sending.current = null;
      if (person.current === sentFor) setBusy(false);
    }
  }, [keyStore, person]);

  const start = useCallback(
    (amountMinorUnits: number) => run(() => startCheckout(transport, keyStore, amountMinorUnits, { redirectPolicy }), apply),
    [run, apply, transport, keyStore, redirectPolicy],
  );

  const resume = useCallback(async () => {
    if (pending) await run(() => sendCheckout(transport, keyStore, pending, { redirectPolicy }), apply);
  }, [run, apply, transport, keyStore, pending, redirectPolicy]);

  const discard = useCallback(async () => {
    // The node holds a reviewed request for an operator: it is never swapped for a fresh payment from here.
    if (!pending || pending.needsReview) return;
    await run(() => discardCheckout(transport, keyStore, pending), applyDiscard);
  }, [run, applyDiscard, transport, keyStore, pending]);

  return {
    capabilities,
    pending,
    intentId,
    initialView,
    refusal,
    notice,
    busy,
    canRedirect: Boolean(options.onRedirect ?? providerEvents.onRedirect),
    start,
    resume,
    discard,
  };
}
