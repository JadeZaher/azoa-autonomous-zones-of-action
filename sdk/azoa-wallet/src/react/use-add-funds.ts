// Add-funds state machine: saved key first, resume before new, resolve by state. See `src/react/AGENTS.md` §add-funds.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, isTerminalCheckout, moneyRefusal, readSavedRequest, sendCheckout, startCheckout } from "../money/index.js";
import type { CheckoutAttempt, CheckoutCapabilitiesView, CheckoutView, MoneyRefusal, SavedRequest } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, redirectTo, settleMoney, useMoneyEvents, useMounted, useStorageChange } from "./internal.js";

export interface AddFundsOptions extends MoneyEventProps {
  /** The intent id the payment page returned with (e.g. `?intent=`); resolved by its state before anything new is offered. */
  returnedIntentId?: string | null;
}

export interface AddFundsState {
  capabilities: CheckoutCapabilitiesView | null;
  /** An unresolved saved payment: offer `resume()` (or `discard()`) before any new payment. */
  pending: SavedRequest | null;
  /** The intent to show and poll. */
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
  discard(): void;
}

export function useAddFunds(options: AddFundsOptions = {}): AddFundsState {
  const { transport, keyStore, messages, events: providerEvents, redirectPolicy } = useMoneyContext();
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
  const sending = useRef(false);
  // Bumped when the subject's key store (or the transport) changes; an answer sent under an older epoch is dropped.
  const epoch = useRef(0);

  useEffect(() => {
    epoch.current += 1;
  }, [transport, keyStore]);

  useEffect(() => {
    let cancelled = false;
    // Everything shown belongs to this key store's subject: a same-tab user switch starts from nothing.
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
      // The answer to this request was lost: it may have been admitted, so it is offered for resume first.
      setPending(saved);
    } else if (saved?.serverId && saved.serverId !== returnedIntentId) {
      if (!returnedIntentId) setIntentId(saved.serverId);
      void settleMoney(transport.checkout.get(saved.serverId)).then(read => {
        if (cancelled) return;
        if (read.ok && isTerminalCheckout(read.value)) {
          // With no return id the status poller shows it (and forgets the key); otherwise it is forgotten here.
          if (returnedIntentId) clearSavedRequest(keyStore, MONEY_SCOPES.checkout, saved.requestKey);
        } else {
          setPending(saved);
        }
      });
    }
    return () => {
      cancelled = true;
    };
  }, [transport, keyStore, returnedIntentId]);

  // Another tab resolved, replaced or lost the saved payment: follow storage instead of offering a stale resume.
  useStorageChange(() => {
    if (sending.current) return;
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
        setNotice(messages.pending.superseded);
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
        setPending(attempt.kept);
        setRefusal(attempt.refusal);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
        return;
    }
  }, [events, mounted, messages, redirectPolicy]);

  const run = useCallback(async (send: () => Promise<CheckoutAttempt>) => {
    // A double click must not send twice (or redirect twice) before the busy state renders.
    if (sending.current) return;
    sending.current = true;
    const sentIn = epoch.current;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      let attempt: CheckoutAttempt;
      try {
        attempt = await send();
      } catch {
        attempt = { kind: "refused", refusal: moneyRefusal("unconfirmed"), kept: readSavedRequest(keyStore, MONEY_SCOPES.checkout) };
      }
      // Host callbacks run through `callHost`, so a throwing `onRedirect` can never turn into a fake "unconfirmed".
      if (epoch.current === sentIn) apply(attempt);
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }, [apply, keyStore]);

  const start = useCallback(
    (amountMinorUnits: number) => run(() => startCheckout(transport, keyStore, amountMinorUnits, { redirectPolicy })),
    [run, transport, keyStore, redirectPolicy],
  );

  const resume = useCallback(async () => {
    if (pending) await run(() => sendCheckout(transport, keyStore, pending, { redirectPolicy }));
  }, [run, transport, keyStore, pending, redirectPolicy]);

  const discard = useCallback(() => {
    if (pending) clearSavedRequest(keyStore, MONEY_SCOPES.checkout, pending.requestKey);
    setPending(null);
    setRefusal(null);
    setNotice(messages.pending.discarded);
  }, [keyStore, pending, messages]);

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
