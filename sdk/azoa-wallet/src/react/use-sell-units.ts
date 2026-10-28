// Host-priced sale: sellable → quote → keyed sell. See `src/react/AGENTS.md` §sale.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, clearSavedRequest, moneyRefusal, readSavedRequest, sendSale, startSale } from "../money/index.js";
import type { MoneyRefusal, SaleAttempt, SaleQuoteView, SaleView, SavedRequest, SellableView } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, settleMoney, useMoneyEvents, useMounted, useStorageChange } from "./internal.js";

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
  const { transport, keyStore, messages } = useMoneyContext();
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  const sale = transport.sale;
  const [sellable, setSellable] = useState<SellableView | null>(null);
  const [quote, setQuote] = useState<SaleQuoteView | null>(null);
  const [pending, setPending] = useState<SavedRequest | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const sending = useRef(false);
  // Bumped per subject; reads and answers that belong to an older one are dropped.
  const epoch = useRef(0);

  const reload = useCallback(async () => {
    if (!sale) return;
    const readIn = epoch.current;
    const result = await settleMoney(sale.sellable());
    if (!mounted.current || epoch.current !== readIn) return;
    if (result.ok) setSellable(result.value);
    else setRefusal(result.refusal);
  }, [sale, mounted]);

  useEffect(() => {
    epoch.current += 1;
    setSellable(null);
    setQuote(null);
    setRefusal(null);
    setNotice(null);
    setPending(sale ? readSavedRequest(keyStore, MONEY_SCOPES.sale) : null);
    if (sale) void reload();
  }, [sale, keyStore, reload]);

  useStorageChange(() => {
    if (sale && !sending.current) setPending(readSavedRequest(keyStore, MONEY_SCOPES.sale));
  });

  const requestQuote = useCallback(async (quantityBaseUnits: string) => {
    if (!sale) return;
    setBusy(true);
    setRefusal(null);
    setNotice(null);
    try {
      const result = await settleMoney(sale.quote({ quantityBaseUnits }));
      if (result.ok) setQuote(result.value);
      else {
        setQuote(null);
        setRefusal(result.refusal);
        callHost(() => events.current.onRefusal?.(result.refusal));
      }
    } finally {
      setBusy(false);
    }
  }, [sale, events]);

  const run = useCallback(async (send: () => Promise<SaleAttempt>) => {
    if (sending.current) return;
    sending.current = true;
    const sentIn = epoch.current;
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
      if (epoch.current !== sentIn) return;
      if (attempt.kind === "pending") setPending(attempt.pending);
      else if (attempt.kind === "superseded") {
        setPending(attempt.current);
        setNotice(messages.pending.superseded);
      } else if (attempt.kind === "accepted") {
        setPending(null);
        setQuote(null);
        setNotice(attempt.view.statusMessage);
        callHost(() => events.current.onSettled?.(attempt.view));
      } else {
        setPending(attempt.kept);
        setRefusal(attempt.refusal);
        callHost(() => events.current.onRefusal?.(attempt.refusal));
      }
      if (attempt.kind !== "pending") await reload();
    } finally {
      sending.current = false;
      setBusy(false);
    }
  }, [events, keyStore, reload, messages]);

  const sell = useCallback(async () => {
    if (quote) await run(() => startSale(transport, keyStore, quote));
  }, [run, transport, keyStore, quote]);

  const resume = useCallback(async () => {
    if (pending) await run(() => sendSale(transport, keyStore, pending));
  }, [run, transport, keyStore, pending]);

  const discard = useCallback(() => {
    if (pending) clearSavedRequest(keyStore, MONEY_SCOPES.sale, pending.requestKey);
    setPending(null);
    setNotice(messages.pending.discarded);
  }, [keyStore, pending, messages]);

  return { supported: Boolean(sale), sellable, quote, pending, refusal, notice, busy, reload, requestQuote, sell, resume, discard };
}
