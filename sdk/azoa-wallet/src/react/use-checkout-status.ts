// Polls one checkout until its money settles, forgetting the saved key once the intent is terminal. See `src/react/AGENTS.md` §checkout-status.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, isSettledCheckout, readSavedRequest, releasesRequestKey, resolveCheckoutByState } from "../money/index.js";
import type { CheckoutView, MoneyRefusal } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import { callHost, settleMoney } from "./internal.js";

export interface CheckoutStatusOptions {
  /** A view already in hand (e.g. from `begin`), shown before the first read; ignored unless it is this intent. */
  initialView?: CheckoutView | null;
  /** Milliseconds between reads; defaults to 2000. */
  pollIntervalMs?: number;
  /** Reads before polling pauses (refresh resumes it); defaults to 150. */
  maxPolls?: number;
  /** The checkout's money reached a final outcome this device was waiting for; read `settlementState` for which. */
  onSettled?: (view: CheckoutView) => void;
}

export interface CheckoutStatusState {
  view: CheckoutView | null;
  refusal: MoneyRefusal | null;
  polling: boolean;
  /** Reads again now and restarts polling. */
  refresh(): void;
}

/** Reads `intentId` until it is settled (or definitely refused). `null` reads nothing. */
export function useCheckoutStatus(intentId: string | null, options: CheckoutStatusOptions = {}): CheckoutStatusState {
  const { transport, keyStore } = useMoneyContext();
  const [view, setView] = useState<CheckoutView | null>(() => viewFor(options.initialView, intentId));
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [polling, setPolling] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [trackedIntentId, setTrackedIntentId] = useState(intentId);
  if (trackedIntentId !== intentId) {
    setTrackedIntentId(intentId);
    setView(viewFor(options.initialView, intentId));
    setRefusal(null);
  }
  const onSettled = useRef(options.onSettled);
  onSettled.current = options.onSettled;
  const initialView = useRef(options.initialView);
  initialView.current = options.initialView;
  // Announce rules (AGENTS.md §checkout-status): once per intent, and only to a device that waited for it.
  const waitedFor = useRef<string | null>(null);
  const announced = useRef<string | null>(null);
  const interval = options.pollIntervalMs ?? 2000;
  const maxPolls = options.maxPolls ?? 150;

  useEffect(() => {
    if (!intentId) return;
    const seed = viewFor(initialView.current, intentId);
    if (seed && !isSettledCheckout(seed)) waitedFor.current = intentId;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let polls = 0;
    const tick = async () => {
      // Whether this device holds the saved key for this intent, read before this answer can forget it.
      const heldKey = readSavedRequest(keyStore, MONEY_SCOPES.checkout)?.serverId === intentId;
      const result = await settleMoney(transport.checkout.get(intentId));
      if (cancelled) return;
      polls += 1;
      if (result.ok) {
        setView(result.value);
        setRefusal(null);
        // A terminal intent's key is forgotten here, though its money may not have settled yet.
        if (resolveCheckoutByState(keyStore, result.value) || heldKey) waitedFor.current = intentId;
        if (isSettledCheckout(result.value)) {
          setPolling(false);
          if (waitedFor.current === intentId && announced.current !== intentId) {
            announced.current = intentId;
            callHost(() => onSettled.current?.(result.value));
          }
          return;
        }
        waitedFor.current = intentId;
      } else {
        setRefusal(result.refusal);
        if (releasesRequestKey(result.refusal)) {
          setPolling(false);
          return;
        }
      }
      if (polls >= maxPolls) {
        setPolling(false);
        return;
      }
      timer = setTimeout(() => void tick(), interval);
    };
    setPolling(true);
    void tick();
    return () => {
      cancelled = true;
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [intentId, transport, keyStore, interval, maxPolls, generation]);

  const refresh = useCallback(() => setGeneration(value => value + 1), []);
  return { view, refusal, polling, refresh };
}

function viewFor(view: CheckoutView | null | undefined, intentId: string | null): CheckoutView | null {
  return view && intentId && view.intentId === intentId ? view : null;
}
