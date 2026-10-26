// Add-funds state machine: saved key first, resume before new, resolve by state. See `src/react/AGENTS.md` §add-funds.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, isFinishedCheckout, readSavedRequest, sendCheckout, startCheckout } from "../money/index.js";
import type { CheckoutAttempt, CheckoutCapabilitiesView, CheckoutView, MoneyRefusal, SavedRequest } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { redirectTo, settleMoney, useMoneyEvents, useMounted } from "./internal.js";

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
  /** Re-sends the pending payment with its original key. */
  resume(): Promise<void>;
  discard(): void;
}

export function useAddFunds(options: AddFundsOptions = {}): AddFundsState {
  const { transport, keyStore, messages, events: providerEvents } = useMoneyContext();
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

  useEffect(() => {
    let cancelled = false;
    void settleMoney(transport.checkout.capabilities()).then(result => {
      if (cancelled) return;
      if (result.ok) setCapabilities(result.value);
      else setRefusal(result.refusal);
    });
    const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (returnedIntentId) setIntentId(returnedIntentId);
    if (saved && !saved.serverId) {
      // The answer to this request was lost: it may have been admitted, so it is offered for resume first.
      setPending(saved);
    } else if (saved?.serverId && saved.serverId !== returnedIntentId) {
      if (!returnedIntentId) setIntentId(saved.serverId);
      void settleMoney(transport.checkout.get(saved.serverId)).then(read => {
        if (cancelled) return;
        if (read.ok && isFinishedCheckout(read.value)) {
          // With no return id, the status poller shows and resolves it; otherwise it is resolved here.
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

  const apply = useCallback((attempt: CheckoutAttempt) => {
    switch (attempt.kind) {
      case "pending":
        setPending(attempt.pending);
        return;
      case "opened":
        setPending(null);
        setInitialView(attempt.view);
        setIntentId(attempt.view.intentId);
        // The intent id is already saved, so leaving the page here is safe.
        redirectTo(events, mounted, attempt.paymentUrl, "checkout");
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
        events.current.onRefusal?.(attempt.refusal);
        return;
    }
  }, [events, mounted, messages]);

  const run = useCallback(async (send: () => Promise<CheckoutAttempt>) => {
    // A double click must not send twice (or redirect twice) before the busy state renders.
    if (sending.current) return;
    sending.current = true;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      apply(await send());
    } catch {
      apply({ kind: "refused", refusal: { code: "unconfirmed", message: "" }, kept: readSavedRequest(keyStore, MONEY_SCOPES.checkout) });
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }, [apply, keyStore]);

  const start = useCallback((amountMinorUnits: number) => run(() => startCheckout(transport, keyStore, amountMinorUnits)), [run, transport, keyStore]);

  const resume = useCallback(async () => {
    if (pending) await run(() => sendCheckout(transport, keyStore, pending));
  }, [run, transport, keyStore, pending]);

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
