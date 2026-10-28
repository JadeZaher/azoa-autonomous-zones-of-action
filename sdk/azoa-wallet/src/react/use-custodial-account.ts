// Account gate state and onboarding actions. See `src/react/AGENTS.md` §hooks.
import { useCallback, useEffect, useSyncExternalStore } from "react";
import type { ConsentRevocationView, CustodialAccountView, KycSessionView, MoneyResult } from "../money/index.js";
import type { AccountSnapshot } from "./account-store.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { callHost, redirectTo, useMoneyEvents, useMounted, usePerson } from "./internal.js";

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
  const { accountStore, events: providerEvents, redirectPolicy } = useMoneyContext();
  const snapshot = useSyncExternalStore(accountStore.subscribe, accountStore.getSnapshot, accountStore.getSnapshot);
  const events = useMoneyEvents(options);
  const mounted = useMounted();
  // The snapshot is already per person (one store each); this only stops a late answer acting for the next person.
  const person = usePerson(() => undefined);
  useEffect(() => accountStore.load(), [accountStore]);

  const ensure = useCallback(async () => {
    const sentFor = person.current;
    const result = await accountStore.ensure();
    if (!result.ok && person.current === sentFor) callHost(() => events.current.onRefusal?.(result.refusal));
    return result;
  }, [accountStore, events, person]);

  const beginKyc = useCallback(async () => {
    const sentFor = person.current;
    const result = await accountStore.beginKyc();
    if (person.current !== sentFor) return result;
    if (result.ok) redirectTo(events, mounted, result.value.verificationUrl, "kyc", redirectPolicy);
    else callHost(() => events.current.onRefusal?.(result.refusal));
    return result;
  }, [accountStore, events, mounted, redirectPolicy, person]);

  const revokeConsent = useCallback(async () => {
    const sentFor = person.current;
    const result = await accountStore.revokeConsent();
    if (!result.ok && person.current === sentFor) callHost(() => events.current.onRefusal?.(result.refusal));
    return result;
  }, [accountStore, events, person]);

  return {
    ...snapshot,
    canRedirect: Boolean(options.onRedirect ?? providerEvents.onRedirect),
    refresh: accountStore.refresh,
    ensure,
    beginKyc,
    revokeConsent,
  };
}
