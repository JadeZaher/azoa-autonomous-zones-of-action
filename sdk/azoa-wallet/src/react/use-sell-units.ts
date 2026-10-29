// Host-priced sale: sellable → quote → keyed sell. See `src/react/AGENTS.md` §sale.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, moneyRefusal, readSavedRequest, sendSale, startSale, toSaleQuoteView, toSellableView } from "../money/index.js";
import type { MoneyRefusal, SaleAttempt, SaleQuoteView, SaleView, SavedRequest, SellableView } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, settleMoney, useMoneyEvents, useMounted, usePerson, useStorageChange } from "./internal.js";

export interface SellUnitsOptions extends MoneyEventProps {
  /** A sale was accepted (never a refused one). */
  onSettled?: (view: SaleView) => void;
}

export interface SellUnitsState {
  /** False when the host's transport has no `sale`; nothing else here does anything then. */
  supported: boolean;
  sellable: SellableView | null;
  quote: SaleQuoteView | null;
  pending: SavedRequest | null;
  refusal: MoneyRefusal | null;
  notice: string | null;
  busy: boolean;
  reload(): Promise<void>;
  requestQuote(quantityBaseUnits: string): Promise<void>;
  /** Sells the current quote under a key bound to its quote id. */
  sell(): Promise<void>;
  resume(): Promise<void>;
  discard(): void;
}

export function useSellUnits(options: SellUnitsOptions = {}): SellUnitsState {
  const { transport, keyStore, messages, identity } = useMoneyContext();
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  const sale = transport.sale;
  const [sellable, setSellable] = useState<SellableView | null>(null);
  const [quote, setQuote] = useState<SaleQuoteView | null>(null);
  const [pending, setPending] = useState<SavedRequest | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // The person whose send is in flight: a same-tick double click is dropped, another person's send is not this one's.
  const sending = useRef<object | null>(null);
  // A new person starts from nothing, from their first paint; reads and answers for the previous one are dropped.
  const person = usePerson(() => {
    setSellable(null);
    setQuote(null);
    setPending(null);
    setRefusal(null);
    setNotice(null);
    setBusy(false);
  });

  const reload = useCallback(async () => {
    if (!sale) return;
    const readFor = person.current;
    const result = await settleMoney(sale.sellable(), toSellableView);
    if (!mounted.current || person.current !== readFor) return;
    if (result.ok) setSellable(result.value);
    else setRefusal(result.refusal);
  }, [sale, mounted, person]);

  useEffect(() => {
    setPending(sale ? readSavedRequest(keyStore, MONEY_SCOPES.sale) : null);
    if (sale) void reload();
  }, [sale, keyStore, reload, identity]);

  useStorageChange(() => {
    if (sale && sending.current !== person.current) setPending(readSavedRequest(keyStore, MONEY_SCOPES.sale));
  });

  const requestQuote = useCallback(async (quantityBaseUnits: string) => {
    if (!sale) return;
    const sentFor = person.current;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      const result = await settleMoney(sale.quote({ quantityBaseUnits }), toSaleQuoteView);
      if (person.current !== sentFor) return;
      if (result.ok) setQuote(result.value);
      else {
        setQuote(null);
        setRefusal(result.refusal);
        callHost(() => events.current.onRefusal?.(result.refusal));
      }
    } finally {
      setBusy(false);
    }
  }, [sale, events, person]);

  const run = useCallback(async (send: () => Promise<SaleAttempt>) => {
    const sentFor = person.current;
    if (sending.current === sentFor) return;
    sending.current = sentFor;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      let attempt: SaleAttempt;
      try {
        attempt = await send();
      } catch {
        attempt = { kind: "refused", refusal: moneyRefusal("unconfirmed"), kept: readSavedRequest(keyStore, MONEY_SCOPES.sale) };
      }
      if (person.current !== sentFor) return;
      if (attempt.kind === "pending") setPending(attempt.pending);
      else if (attempt.kind === "superseded") {
        setPending(attempt.current);
        setNotice(attempt.current ? messages.pending.replaced : messages.pending.superseded);
      } else if (attempt.kind === "accepted") {
        setPending(null);
        setQuote(null);
        setNotice(attempt.view.statusMessage);
        callHost(() => events.current.onSettled?.(attempt.view));
      } else {
        setPending(attempt.kept);
        // A request held for review is explained by its own notice.
        if (!attempt.kept?.needsReview) setRefusal(attempt.refusal);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
      }
      if (attempt.kind !== "pending") await reload();
    } finally {
      if (sending.current === sentFor) sending.current = null;
      if (person.current === sentFor) setBusy(false);
    }
  }, [events, keyStore, reload, messages, person]);

  const sell = useCallback(async () => {
    if (quote) await run(() => startSale(transport, keyStore, quote));
  }, [run, transport, keyStore, quote]);

  const resume = useCallback(async () => {
    if (pending) await run(() => sendSale(transport, keyStore, pending));
  }, [run, transport, keyStore, pending]);

  const discard = useCallback(() => {
    // A request the host holds for review is never swapped for a fresh one from here.
    if (!pending || pending.needsReview) return;
    clearSavedRequest(keyStore, MONEY_SCOPES.sale, pending.requestKey);
    setPending(null);
    setNotice(messages.pending.discarded);
  }, [keyStore, pending, messages]);

  return { supported: Boolean(sale), sellable, quote, pending, refusal, notice, busy, reload, requestQuote, sell, resume, discard };
}
