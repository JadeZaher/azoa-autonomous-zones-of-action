// Cash-out state machine: availability, payout destination, keyed start, history. See `src/react/AGENTS.md` §cash-out.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, isCashOutOnTrack, moneyRefusal, readSavedRequest, sendCashOut, startCashOut } from "../money/index.js";
import type { CashOutAttempt, CashOutQuoteView, CashOutView, MoneyRefusal, PayoutDestinationView, SavedRequest } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, redirectTo, settleMoney, useMoneyEvents, useMounted, useStorageChange } from "./internal.js";

export interface CashOutOptions extends MoneyEventProps {
  /** A cash-out was accepted (never a refused one); its `status` says how far it got. */
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
  /** `success` while the accepted cash-out is on its way or paid; `warning` when it needs the person's attention. */
  noticeTone: "success" | "warning" | "info";
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
  const { transport, keyStore, messages, events: providerEvents, redirectPolicy } = useMoneyContext();
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  const [quote, setQuote] = useState<CashOutQuoteView | null>(null);
  const [destination, setDestination] = useState<PayoutDestinationView | null>(null);
  const [history, setHistory] = useState<CashOutView[]>([]);
  const [pending, setPending] = useState<SavedRequest | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [destinationRefusal, setDestinationRefusal] = useState<MoneyRefusal | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [noticeTone, setNoticeTone] = useState<CashOutState["noticeTone"]>("info");
  const [busy, setBusy] = useState(false);
  const sending = useRef(false);
  // Bumped per subject; reads and answers that belong to an older one are dropped.
  const epoch = useRef(0);

  const reload = useCallback(async () => {
    const readIn = epoch.current;
    const [available, payoutDestination, rows] = await Promise.all([
      settleMoney(transport.cashOut.availability()),
      settleMoney(transport.cashOut.destination()),
      settleMoney(transport.cashOut.history()),
    ]);
    if (!mounted.current || epoch.current !== readIn) return;
    if (available.ok) setQuote(available.value);
    else setRefusal(available.refusal);
    if (payoutDestination.ok) setDestination(payoutDestination.value);
    if (rows.ok && Array.isArray(rows.value)) setHistory(rows.value);
  }, [transport, mounted]);

  useEffect(() => {
    epoch.current += 1;
    // A same-tab user switch never shows the last person's cash-outs, even for a moment.
    setQuote(null);
    setDestination(null);
    setHistory([]);
    setRefusal(null);
    setDestinationRefusal(null);
    setNotice(null);
    setPending(readSavedRequest(keyStore, MONEY_SCOPES.cashOut));
    void reload();
  }, [keyStore, reload]);

  useStorageChange(() => {
    if (!sending.current) setPending(readSavedRequest(keyStore, MONEY_SCOPES.cashOut));
  });

  const run = useCallback(async (send: () => Promise<CashOutAttempt>) => {
    if (sending.current) return;
    sending.current = true;
    const sentIn = epoch.current;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      let attempt: CashOutAttempt;
      try {
        attempt = await send();
      } catch {
        attempt = { kind: "refused", refusal: moneyRefusal("unconfirmed"), kept: readSavedRequest(keyStore, MONEY_SCOPES.cashOut) };
      }
      if (epoch.current !== sentIn) return;
      if (attempt.kind === "pending") setPending(attempt.pending);
      else if (attempt.kind === "superseded") {
        setPending(attempt.current);
        setNoticeTone("info");
        setNotice(messages.pending.superseded);
      } else if (attempt.kind === "accepted") {
        setPending(null);
        setNoticeTone(isCashOutOnTrack(attempt.view) ? "success" : "warning");
        setNotice(attempt.view.statusMessage);
        callHost(() => events.current.onSettled?.(attempt.view));
      } else {
        setPending(attempt.kept);
        setRefusal(attempt.refusal);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
      }
      // History is re-read after every attempt, so a claim whose answer was lost is visible before the person decides.
      if (attempt.kind !== "pending") await reload();
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }, [events, keyStore, reload, messages]);

  const start = useCallback((amountMinorUnits: number) => run(() => startCashOut(transport, keyStore, amountMinorUnits)), [run, transport, keyStore]);

  const resume = useCallback(async () => {
    if (pending) await run(() => sendCashOut(transport, keyStore, pending));
  }, [run, transport, keyStore, pending]);

  const discard = useCallback(() => {
    if (pending) clearSavedRequest(keyStore, MONEY_SCOPES.cashOut, pending.requestKey);
    setPending(null);
    setNoticeTone("info");
    setNotice(messages.pending.discarded);
  }, [keyStore, pending, messages]);

  const beginDestinationSetup = useCallback(async () => {
    setBusy(true);
    setDestinationRefusal(null);
    try {
      const result = await settleMoney(transport.cashOut.beginDestinationSetup());
      if (result.ok) {
        setDestination(result.value);
        redirectTo(events, mounted, result.value.setupUrl, "payout-setup", redirectPolicy);
      } else {
        setDestinationRefusal(result.refusal);
        callHost(() => events.current.onRefusal?.(result.refusal));
      }
    } finally {
      setBusy(false);
    }
  }, [transport, events, mounted, redirectPolicy]);

  return {
    quote,
    destination,
    history,
    pending,
    refusal,
    destinationRefusal,
    notice,
    noticeTone,
    busy,
    canRedirect: Boolean(options.onRedirect ?? providerEvents.onRedirect),
    reload,
    start,
    resume,
    discard,
    beginDestinationSetup,
  };
}
