// Shared plumbing for the money hooks and components. See `src/react/AGENTS.md`.
import { useEffect, useRef } from "react";
import type { MutableRefObject } from "react";
import { safeRedirectUrl } from "../money/index.js";
import type { MoneyRedirectKind } from "../money/index.js";
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

/** Hands a safe URL to the host's `onRedirect`; false when there is no handler, no safe URL, or the component is gone. */
export function redirectTo(events: MutableRefObject<MoneyEventProps>, mounted: MutableRefObject<boolean>, url: string | null | undefined, kind: MoneyRedirectKind): boolean {
  const safe = safeRedirectUrl(url);
  const handler = events.current.onRedirect;
  if (!safe || !handler || !mounted.current) return false;
  handler(safe, kind);
  return true;
}

/** Slots from the provider, overridden per component. */
export function useSlots(local?: Partial<MoneySlots>): MoneySlots {
  const { slots } = useMoneyContext();
  return local ? { ...slots, ...local } : slots;
}

/** Locale date-time for display; the raw text when it does not parse. */
export function formatDateTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}
