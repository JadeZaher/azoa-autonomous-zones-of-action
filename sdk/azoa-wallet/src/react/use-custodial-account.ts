// Account gate state and onboarding actions. See `src/react/AGENTS.md` §hooks.
import { useCallback, useEffect, useSyncExternalStore } from "react";
import type { ConsentRevocationView, CustodialAccountView, KycSessionView, MoneyResult } from "../money/index.js";
import type { AccountSnapshot } from "./account-store.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { redirectTo, useMoneyEvents, useMounted } from "./internal.js";

export interface CustodialAccountState extends AccountSnapshot {
  /** Whether a host `onRedirect` exists; without one, components render plain links instead. */
  canRedirect: boolean;
  refresh(): Promise<void>;
  ensure(): Promise<MoneyResult<CustodialAccountView>>;
  /** Ensures the account, opens identity verification and hands its URL to `onRedirect(url, "kyc")`. */
  beginKyc(): Promise<MoneyResult<KycSessionView>>;
  revokeConsent(): Promise<MoneyResult<ConsentRevocationView>>;
}

/** The shared account read: `gate` is `ready` | `setup` | `review` | `revoked` | `unavailable` (or `loading`). */
export function useCustodialAccount(options: MoneyEventProps = {}): CustodialAccountState {
  const { accountStore, events: providerEvents } = useMoneyContext();
  const snapshot = useSyncExternalStore(accountStore.subscribe, accountStore.getSnapshot, accountStore.getSnapshot);
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  useEffect(() => accountStore.load(), [accountStore]);

  const ensure = useCallback(async () => {
    const result = await accountStore.ensure();
    if (!result.ok) events.current.onRefusal?.(result.refusal);
    return result;
  }, [accountStore, events]);

  const beginKyc = useCallback(async () => {
    const result = await accountStore.beginKyc();
    if (result.ok) redirectTo(events, mounted, result.value.verificationUrl, "kyc");
    else events.current.onRefusal?.(result.refusal);
    return result;
  }, [accountStore, events, mounted]);

  const revokeConsent = useCallback(async () => {
    const result = await accountStore.revokeConsent();
    if (!result.ok) events.current.onRefusal?.(result.refusal);
    return result;
  }, [accountStore, events]);

  return {
    ...snapshot,
    canRedirect: Boolean(options.onRedirect ?? providerEvents.onRedirect),
    refresh: accountStore.refresh,
    ensure,
    beginKyc,
    revokeConsent,
  };
}
