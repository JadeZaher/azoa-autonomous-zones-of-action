// Polls one checkout until it finishes and resolves the saved key by its state. See `src/react/AGENTS.md` §checkout-status.
import { useCallback, useEffect, useRef, useState } from "react";
import { MONEY_SCOPES, isFinishedCheckout, readSavedRequest, releasesRequestKey, resolveCheckoutByState } from "../money/index.js";
import type { CheckoutView, MoneyRefusal } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import { settleMoney } from "./internal.js";

export interface CheckoutStatusOptions {
  /** A view already in hand (e.g. from `begin`), shown before the first read. */
  initialView?: CheckoutView | null;
  /** Milliseconds between reads; defaults to 2000. */
  pollIntervalMs?: number;
  /** Reads before polling pauses (refresh resumes it); defaults to 150. */
  maxPolls?: number;
  /** The checkout reached a definite outcome this device was waiting for; read `settlementState` for which. */
  onSettled?: (view: CheckoutView) => void;
}

export interface CheckoutStatusState {
  view: CheckoutView | null;
  refusal: MoneyRefusal | null;
  polling: boolean;
  /** Reads again now and restarts polling. */
  refresh(): void;
}

/** Reads `intentId` until it is finished (or definitely refused). `null` reads nothing. */
export function useCheckoutStatus(intentId: string | null, options: CheckoutStatusOptions = {}): CheckoutStatusState {
  const { transport, keyStore } = useMoneyContext();
  const [view, setView] = useState<CheckoutView | null>(options.initialView ?? null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [polling, setPolling] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [trackedIntentId, setTrackedIntentId] = useState(intentId);
  if (trackedIntentId !== intentId) {
    setTrackedIntentId(intentId);
    setView(options.initialView ?? null);
    setRefusal(null);
  }
  const onSettled = useRef(options.onSettled);
  onSettled.current = options.onSettled;
  // Settlement is announced once per intent, and only to a device that was waiting for it (see AGENTS.md).
  const sawUnfinishedFor = useRef(options.initialView && !isFinishedCheckout(options.initialView) ? options.initialView.intentId : null);
  const announced = useRef<string | null>(null);
  const interval = options.pollIntervalMs ?? 2000;
  const maxPolls = options.maxPolls ?? 150;

  useEffect(() => {
    if (!intentId) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let polls = 0;
    const tick = async () => {
      // Whether this device holds an unresolved key for this intent, read before the state can clear it.
      const waitedHere = readSavedRequest(keyStore, MONEY_SCOPES.checkout)?.serverId === intentId;
      const result = await settleMoney(transport.checkout.get(intentId));
      if (cancelled) return;
      polls += 1;
      if (result.ok) {
        setView(result.value);
        setRefusal(null);
        if (isFinishedCheckout(result.value)) {
          const resolvedKey = resolveCheckoutByState(keyStore, result.value);
          setPolling(false);
          if ((sawUnfinishedFor.current === intentId || resolvedKey || waitedHere) && announced.current !== intentId) {
            announced.current = intentId;
            onSettled.current?.(result.value);
          }
          return;
        }
        sawUnfinishedFor.current = intentId;
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
