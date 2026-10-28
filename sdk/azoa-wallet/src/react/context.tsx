// `<AzoaMoneyProvider>`: the transport, key store, copy, redirect policy and slots every money hook reads. See `src/react/AGENTS.md` §provider.
import { createContext, useContext, useMemo } from "react";
import type { ReactNode } from "react";
import { createKeyStore, mergeMoneyMessages } from "../money/index.js";
import type {
  AzoaMoneyTransport,
  KeyStore,
  MoneyMessages,
  MoneyMessagesOverride,
  MoneyRedirectKind,
  MoneyRefusal,
  RedirectPolicy,
} from "../money/index.js";
import { AccountStore } from "./account-store.js";
import { defaultMoneySlots } from "./slots.js";
import type { MoneySlots } from "./slots.js";

/** Events the host handles; the SDK never navigates by itself. */
export interface MoneyEventProps {
  /** Navigate to a hosted page (payment, identity verification, payout setup). */
  onRedirect?: (url: string, kind: MoneyRedirectKind) => void;
  /** A person's action was refused. */
  onRefusal?: (refusal: MoneyRefusal) => void;
}

export interface AzoaMoneyProviderProps extends MoneyEventProps {
  /** Every privileged call goes through this; keep it referentially stable (e.g. `useMemo`). */
  transport: AzoaMoneyTransport;
  /** Where saved request keys live; defaults to `createKeyStore({ subject })` over `localStorage`. Bind it to one subject. */
  keyStore?: KeyStore;
  /** The host session's subject (e.g. its user id); required when `keyStore` is omitted. Changing it resets every money view, even with a stable `keyStore`. */
  subject?: string;
  messages?: MoneyMessagesOverride;
  slots?: Partial<MoneySlots>;
  /** Exact origins redirects and links may also go to, on top of `DEFAULT_REDIRECT_ORIGINS` (a Stripe custom domain, your Veriff or hosted-KYC origin, your own pages). */
  allowedRedirectOrigins?: readonly string[];
  /** Development only: also admit `http:` redirects to loopback (`localhost`, `127.0.0.1`, `[::1]`). */
  allowLoopbackRedirects?: boolean;
  children?: ReactNode;
}

export interface MoneyContextValue {
  transport: AzoaMoneyTransport;
  keyStore: KeyStore;
  messages: MoneyMessages;
  slots: MoneySlots;
  events: MoneyEventProps;
  accountStore: AccountStore;
  redirectPolicy: RedirectPolicy;
  /** A fresh token whenever the person changes (transport, key store or subject); hooks reset on it and drop older answers. */
  identity: object;
}

const MoneyContext = createContext<MoneyContextValue | null>(null);

export function AzoaMoneyProvider(props: AzoaMoneyProviderProps) {
  const { transport, keyStore, subject, messages, slots, onRedirect, onRefusal, allowedRedirectOrigins, allowLoopbackRedirects, children } = props;
  // Without a subject there is no safe place for keys: createKeyStore throws rather than share them across users.
  const resolvedKeyStore = useMemo(() => keyStore ?? createKeyStore({ subject: subject ?? "" }), [keyStore, subject]);
  // A new subject is a new person even when the host keeps one session-aware `keyStore` object (LOW-3).
  const identity = useMemo(() => ({}), [transport, resolvedKeyStore, subject]);
  // The store (wallet address, a live verification link) is rebuilt per person, never carried over.
  const accountStore = useMemo(() => new AccountStore(transport), [transport, identity]);
  const originsKey = allowedRedirectOrigins?.join(" ");
  const redirectPolicy = useMemo<RedirectPolicy>(
    () => ({ ...(allowedRedirectOrigins ? { allowedOrigins: [...allowedRedirectOrigins] } : {}), allowLoopbackHttp: allowLoopbackRedirects === true }),
    // Keyed on the origins' text, so an inline array literal does not rebuild the policy every render.
    [originsKey, allowLoopbackRedirects],
  );
  const value = useMemo<MoneyContextValue>(() => ({
    transport,
    keyStore: resolvedKeyStore,
    messages: mergeMoneyMessages(messages),
    slots: { ...defaultMoneySlots, ...slots },
    events: { onRedirect, onRefusal },
    accountStore,
    redirectPolicy,
    identity,
  }), [transport, resolvedKeyStore, messages, slots, onRedirect, onRefusal, accountStore, redirectPolicy, identity]);
  return <MoneyContext.Provider value={value}>{children}</MoneyContext.Provider>;
}

/** The provider's value; throws outside `<AzoaMoneyProvider>`. */
export function useMoneyContext(): MoneyContextValue {
  const value = useContext(MoneyContext);
  if (!value) throw new Error("azoa-sdk/react: money components and hooks must render inside <AzoaMoneyProvider>.");
  return value;
}
