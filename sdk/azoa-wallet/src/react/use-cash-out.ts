// Cash-out state machine: availability, payout destination, keyed start, history. See `src/react/AGENTS.md` §cash-out.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, readSavedRequest, sendCashOut, startCashOut } from "../money/index.js";
import type { CashOutAttempt, CashOutQuoteView, CashOutView, MoneyRefusal, PayoutDestinationView, SavedRequest } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { redirectTo, settleMoney, useMoneyEvents, useMounted } from "./internal.js";

export interface CashOutOptions extends MoneyEventProps {
  /** A cash-out was accepted; its `status` says how far it got. */
  onSettled?: (view: CashOutView) => void;
}

export interface CashOutState {
  quote: CashOutQuoteView | null;
  destination: PayoutDestinationView | null;
  history: CashOutView[];
  pending: SavedRequest | null;
  refusal: MoneyRefusal | null;
  /** The refusal from the last payout-destination setup (e.g. `account_holder_required`). */
  destinationRefusal: MoneyRefusal | null;
  notice: string | null;
  busy: boolean;
  canRedirect: boolean;
  reload(): Promise<void>;
  start(amountMinorUnits: number): Promise<void>;
  resume(): Promise<void>;
  discard(): void;
  /** Opens (or resumes) payout-destination setup and hands its URL to `onRedirect(url, "payout-setup")`. */
  beginDestinationSetup(): Promise<void>;
}

export function useCashOut(options: CashOutOptions = {}): CashOutState {
  const { transport, keyStore, messages, events: providerEvents } = useMoneyContext();
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  const [quote, setQuote] = useState<CashOutQuoteView | null>(null);
  const [destination, setDestination] = useState<PayoutDestinationView | null>(null);
  const [history, setHistory] = useState<CashOutView[]>([]);
  const [pending, setPending] = useState<SavedRequest | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [destinationRefusal, setDestinationRefusal] = useState<MoneyRefusal | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const sending = useRef(false);

  const reload = useCallback(async () => {
    const [available, payoutDestination, rows] = await Promise.all([
      settleMoney(transport.cashOut.availability()),
      settleMoney(transport.cashOut.destination()),
      settleMoney(transport.cashOut.history()),
    ]);
    if (!mounted.current) return;
    if (available.ok) setQuote(available.value);
    else setRefusal(available.refusal);
    if (payoutDestination.ok) setDestination(payoutDestination.value);
    if (rows.ok) setHistory(rows.value);
  }, [transport, mounted]);

  useEffect(() => {
    setPending(readSavedRequest(keyStore, MONEY_SCOPES.cashOut));
    void reload();
  }, [keyStore, reload]);

  const run = useCallback(async (send: () => Promise<CashOutAttempt>) => {
    if (sending.current) return;
    sending.current = true;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    let attempt: CashOutAttempt;
    try {
      attempt = await send();
    } catch {
      attempt = { kind: "refused", refusal: { code: "unconfirmed", message: "" }, kept: readSavedRequest(keyStore, MONEY_SCOPES.cashOut) };
    }
    if (attempt.kind === "pending") setPending(attempt.pending);
    else if (attempt.kind === "accepted") {
      setPending(null);
      setNotice(attempt.view.statusMessage);
      events.current.onSettled?.(attempt.view);
    } else {
      setPending(attempt.kept);
      setRefusal(attempt.refusal);
      events.current.onRefusal?.(attempt.refusal);
    }
    // History is re-read after every attempt, so a claim whose answer was lost is visible before the person decides.
    if (attempt.kind !== "pending") await reload();
    sending.current = false;
    setBusy(false);
  }, [events, keyStore, reload]);

  const start = useCallback((amountMinorUnits: number) => run(() => startCashOut(transport, keyStore, amountMinorUnits)), [run, transport, keyStore]);

  const resume = useCallback(async () => {
    if (pending) await run(() => sendCashOut(transport, keyStore, pending));
  }, [run, transport, keyStore, pending]);

  const discard = useCallback(() => {
    if (pending) clearSavedRequest(keyStore, MONEY_SCOPES.cashOut, pending.requestKey);
    setPending(null);
    setNotice(messages.pending.discarded);
  }, [keyStore, pending, messages]);

  const beginDestinationSetup = useCallback(async () => {
    setBusy(true);
    setDestinationRefusal(null);
    const result = await settleMoney(transport.cashOut.beginDestinationSetup());
    setBusy(false);
    if (result.ok) {
      setDestination(result.value);
      redirectTo(events, mounted, result.value.setupUrl, "payout-setup");
    } else {
      setDestinationRefusal(result.refusal);
      events.current.onRefusal?.(result.refusal);
    }
  }, [transport, events, mounted]);

  return {
    quote,
    destination,
    history,
    pending,
    refusal,
    destinationRefusal,
    notice,
    busy,
    canRedirect: Boolean(options.onRedirect ?? providerEvents.onRedirect),
    reload,
    start,
    resume,
    discard,
    beginDestinationSetup,
  };
}
