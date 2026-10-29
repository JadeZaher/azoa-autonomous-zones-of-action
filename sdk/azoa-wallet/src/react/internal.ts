// Shared plumbing for the money hooks and components. See `src/react/AGENTS.md`.
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import type { MutableRefObject } from "react";
import { formatMinorUnits, safeRedirectUrl } from "../money/index.js";
import type { MoneyMessages, MoneyRedirectKind, RedirectPolicy } from "../money/index.js";
import type { MoneySlots } from "./slots.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
export { settleMoney } from "./settle.js";

/** Latest event handlers: the component's own, else the provider's. Read at call time, so handlers never retrigger effects. */
export function useMoneyEvents<Extra extends object = object>(local: MoneyEventProps & Extra): MutableRefObject<MoneyEventProps & Extra> {
  const { events } = useMoneyContext();
  const merged = { ...local, onRedirect: local.onRedirect ?? events.onRedirect, onRefusal: local.onRefusal ?? events.onRefusal };
  const ref = useRef(merged);
  ref.current = merged;
  return ref;
}

/** `useLayoutEffect` in a browser, `useEffect` during SSR (React 18 warns about layout effects on the server). */
const useCommitEffect = typeof window === "undefined" ? useEffect : useLayoutEffect;

/** The provider's current person: state resets during render (before a new person's first paint); the ref names the committed person. See `src/react/AGENTS.md` §provider. */
export function usePerson(onChange: () => void): MutableRefObject<object> {
  const { identity } = useMoneyContext();
  const [shownFor, setShownFor] = useState(identity);
  if (shownFor !== identity) {
    setShownFor(identity);
    onChange();
  }
  const current = useRef(identity);
  useCommitEffect(() => {
    current.current = identity;
  }, [identity]);
  return current;
}

/** False once the component unmounts, so a late answer never navigates a page the person already left. */
export function useMounted(): MutableRefObject<boolean> {
  const mounted = useRef(false);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  return mounted;
}

/** Runs a host callback; if it throws, the error is logged and the hook's own state (busy, pending) is unaffected. */
export function callHost(run: () => void): void {
  try {
    run();
  } catch (error) {
    console.error("azoa-sdk/react: a host callback threw; the money state is unaffected.", error);
  }
}

/** Hands a policy-admitted URL to the host's `onRedirect`; false when there is no handler, no admitted URL, or the component is gone. */
export function redirectTo(
  events: MutableRefObject<MoneyEventProps>,
  mounted: MutableRefObject<boolean>,
  url: string | null | undefined,
  kind: MoneyRedirectKind,
  policy?: RedirectPolicy,
): boolean {
  const safe = safeRedirectUrl(url, policy);
  const handler = events.current.onRedirect;
  if (!safe || !handler || !mounted.current) return false;
  callHost(() => handler(safe, kind));
  return true;
}

/** Re-runs `onChange` when another tab writes storage (`storage` events fire only in the other tabs). */
export function useStorageChange(onChange: () => void): void {
  const latest = useRef(onChange);
  latest.current = onChange;
  useEffect(() => {
    if (typeof window === "undefined" || typeof window.addEventListener !== "function") return;
    const listener = () => latest.current();
    window.addEventListener("storage", listener);
    return () => window.removeEventListener("storage", listener);
  }, []);
}

/** Slots from the provider, overridden per component. */
export function useSlots(local?: Partial<MoneySlots>): MoneySlots {
  const { slots } = useMoneyContext();
  return local ? { ...slots, ...local } : slots;
}

/** An amount for display; the copy map's placeholder when the currency is unknown (never a guessed decimal point). */
export function displayAmount(minorUnits: number, currencyCode: string | null | undefined, messages: MoneyMessages): string {
  return formatMinorUnits(minorUnits, currencyCode) ?? messages.common.amountUnavailable;
}

/** Locale date-time for display; the raw text when it does not parse. */
export function formatDateTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}
