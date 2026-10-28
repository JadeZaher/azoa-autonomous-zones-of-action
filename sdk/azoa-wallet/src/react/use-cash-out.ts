// Cash-out state machine: availability, payout destination, keyed start, history. See `src/react/AGENTS.md` §cash-out.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, isCashOutOnTrack, moneyRefusal, readSavedRequest, sendCashOut, startCashOut } from "../money/index.js";
import type { CashOutAttempt, CashOutQuoteView, CashOutView, MoneyRefusal, PayoutDestinationView, SavedRequest } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, redirectTo, settleMoney, useMoneyEvents, useMounted, usePerson, useStorageChange } from "./internal.js";

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
  const { transport, keyStore, messages, events: providerEvents, redirectPolicy, identity } = useMoneyContext();
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
  // The person whose send is in flight: a same-tick double click is dropped, another person's send is not this one's.
  const sending = useRef<object | null>(null);
  // A same-tab user switch never shows the last person's cash-outs, even for one paint; their late answers are dropped.
  const person = usePerson(() => {
    setQuote(null);
    setDestination(null);
    setHistory([]);
    setPending(null);
    setRefusal(null);
    setDestinationRefusal(null);
    setNotice(null);
    setBusy(false);
  });

  const reload = useCallback(async () => {
    const readFor = person.current;
    const [available, payoutDestination, rows] = await Promise.all([
      settleMoney(transport.cashOut.availability()),
      settleMoney(transport.cashOut.destination()),
      settleMoney(transport.cashOut.history()),
    ]);
    if (!mounted.current || person.current !== readFor) return;
    if (available.ok) setQuote(available.value);
    else setRefusal(available.refusal);
    if (payoutDestination.ok) setDestination(payoutDestination.value);
    if (rows.ok && Array.isArray(rows.value)) setHistory(rows.value);
  }, [transport, mounted, person]);

  useEffect(() => {
    setPending(readSavedRequest(keyStore, MONEY_SCOPES.cashOut));
    void reload();
  }, [keyStore, reload, identity]);

  useStorageChange(() => {
    if (sending.current !== person.current) setPending(readSavedRequest(keyStore, MONEY_SCOPES.cashOut));
  });

  const run = useCallback(async (send: () => Promise<CashOutAttempt>) => {
    const sentFor = person.current;
    if (sending.current === sentFor) return;
    sending.current = sentFor;
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
      if (person.current !== sentFor) return;
      if (attempt.kind === "pending") setPending(attempt.pending);
      else if (attempt.kind === "superseded") {
        setPending(attempt.current);
        setNoticeTone("info");
        setNotice(attempt.current ? messages.pending.replaced : messages.pending.superseded);
      } else if (attempt.kind === "accepted") {
        setPending(null);
        setNoticeTone(isCashOutOnTrack(attempt.view) ? "success" : "warning");
        setNotice(attempt.view.statusMessage);
        callHost(() => events.current.onSettled?.(attempt.view));
      } else {
        setPending(attempt.kept);
        // A request held for review is explained by its own notice.
        if (!attempt.kept?.needsReview) setRefusal(attempt.refusal);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
      }
      // History is re-read after every attempt, so a claim whose answer was lost is visible before the person decides.
      if (attempt.kind !== "pending") await reload();
    } finally {
      if (sending.current === sentFor) sending.current = null;
      if (person.current === sentFor) setBusy(false);
    }
  }, [events, keyStore, reload, messages, person]);

  const start = useCallback((amountMinorUnits: number) => run(() => startCashOut(transport, keyStore, amountMinorUnits)), [run, transport, keyStore]);

  const resume = useCallback(async () => {
    if (pending) await run(() => sendCashOut(transport, keyStore, pending));
  }, [run, transport, keyStore, pending]);

  const discard = useCallback(() => {
    // The node holds a reviewed request for an operator: it is never swapped for a fresh one from here.
    if (!pending || pending.needsReview) return;
    clearSavedRequest(keyStore, MONEY_SCOPES.cashOut, pending.requestKey);
    setPending(null);
    setNoticeTone("info");
    setNotice(messages.pending.discarded);
  }, [keyStore, pending, messages]);

  const beginDestinationSetup = useCallback(async () => {
    const sentFor = person.current;
    setBusy(true);
    setDestinationRefusal(null);
    try {
      const result = await settleMoney(transport.cashOut.beginDestinationSetup());
      // The previous person's setup page is never opened for the next one.
      if (person.current !== sentFor) return;
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
  }, [transport, events, mounted, redirectPolicy, person]);

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
