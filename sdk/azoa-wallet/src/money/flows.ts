// Framework-free keyed money flows: lock → claim → persist → send → resolve the key by the answer. See `src/money/AGENTS.md` §flows.
import { SdkError } from "../core/errors.js";
import { isOpenCheckout, isTerminalCheckout, safeRedirectUrl } from "./presentation.js";
import type { RedirectPolicy } from "./presentation.js";
import { moneyRefusal, releasesRequestKey } from "./refusal.js";
import { MONEY_SCOPES, claimRequestKey, clearSavedRequest, readSavedRequest, rememberServerId } from "./request-key.js";
import type { KeyStore, SavedRequest } from "./request-key.js";
import type { AzoaMoneyTransport, CashOutView, CheckoutView, MoneyRefusal, MoneyResult, SaleQuoteView, SaleView } from "./types.js";

/** A refused attempt; `kept` is the saved request when the refusal may hide an admission (resume it, never replace it). */
export type RefusedAttempt = { kind: "refused"; refusal: MoneyRefusal; kept: SavedRequest | null };

/** A different unresolved request is saved; it must be resumed or discarded first. */
export type PendingAttempt = { kind: "pending"; pending: SavedRequest };

/** The request being resent is no longer the saved one (another tab resolved or replaced it); nothing was sent. `current` is what is saved now. */
export type SupersededAttempt = { kind: "superseded"; current: SavedRequest | null };

/** `opened`: pay at `paymentUrl` (the key now remembers the intent id). `finished`: the key's intent is terminal (its money may still be settling). */
export type CheckoutAttempt =
  | PendingAttempt
  | SupersededAttempt
  | { kind: "opened"; view: CheckoutView; paymentUrl: string | null }
  | { kind: "finished"; view: CheckoutView }
  | RefusedAttempt;

export type CashOutAttempt = PendingAttempt | SupersededAttempt | { kind: "accepted"; view: CashOutView } | RefusedAttempt;

export type SaleAttempt = PendingAttempt | SupersededAttempt | { kind: "accepted"; view: SaleView } | RefusedAttempt;

export interface CheckoutFlowOptions {
  /** Which payment pages `paymentUrl` may name; defaults to any `https:` origin. */
  redirectPolicy?: RedirectPolicy;
}

/** Claims (or reuses) a checkout key, persists it, then begins the checkout; one tab at a time where Web Locks exist. */
export function startCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, amountMinorUnits: number, options: CheckoutFlowOptions = {}): Promise<CheckoutAttempt> {
  return withRequestLock(MONEY_SCOPES.checkout, async () => {
    const claimed = claimKey(keyStore, MONEY_SCOPES.checkout, amountMinorUnits);
    return claimed.kind === "ready" ? beginCheckout(transport, keyStore, claimed.request, options) : claimed;
  });
}

/** Resends a saved checkout with its original key, only while it is still the saved one; otherwise `superseded`. */
export function sendCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: CheckoutFlowOptions = {}): Promise<CheckoutAttempt> {
  return withRequestLock(MONEY_SCOPES.checkout, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginCheckout(transport, keyStore, current, options);
  });
}

/** Forgets the saved checkout key when `view` is its terminal intent; true when it did. Polling continues until `isSettledCheckout`. */
export function resolveCheckoutByState(keyStore: KeyStore, view: CheckoutView): boolean {
  if (!isTerminalCheckout(view)) return false;
  const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
  if (saved?.serverId !== view.intentId) return false;
  clearSavedRequest(keyStore, MONEY_SCOPES.checkout, saved.requestKey);
  return true;
}

/** Claims (or reuses) a cash-out key, persists it, then starts the cash-out. */
export function startCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, amountMinorUnits: number): Promise<CashOutAttempt> {
  return withRequestLock(MONEY_SCOPES.cashOut, async () => {
    const claimed = claimKey(keyStore, MONEY_SCOPES.cashOut, amountMinorUnits);
    return claimed.kind === "ready" ? beginCashOut(transport, keyStore, claimed.request) : claimed;
  });
}

/** Resends a saved cash-out with its original key, only while it is still the saved one. */
export function sendCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<CashOutAttempt> {
  return withRequestLock(MONEY_SCOPES.cashOut, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.cashOut);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginCashOut(transport, keyStore, current);
  });
}

/** Claims a sale key bound to the quote (its id is replayed on resume), persists it, then sells. */
export function startSale(transport: AzoaMoneyTransport, keyStore: KeyStore, quote: SaleQuoteView): Promise<SaleAttempt> {
  return withRequestLock(MONEY_SCOPES.sale, async () => {
    const claimed = claimKey(keyStore, MONEY_SCOPES.sale, quote.proceedsMinorUnits, quote.quoteId);
    return claimed.kind === "ready" ? beginSale(transport, keyStore, claimed.request) : claimed;
  });
}

/** Resends a saved sale (same key, same quote id), only while it is still the saved one. */
export function sendSale(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<SaleAttempt> {
  return withRequestLock(MONEY_SCOPES.sale, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.sale);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginSale(transport, keyStore, current);
  });
}

async function beginCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: CheckoutFlowOptions): Promise<CheckoutAttempt> {
  const result = await settled(() => transport.checkout.begin({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits }));
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.checkout, request, result.refusal);
  const view = result.value;
  if (!isRecord(view) || typeof view.intentId !== "string") return refused(keyStore, MONEY_SCOPES.checkout, request, moneyRefusal("unconfirmed"));
  if (isTerminalCheckout(view)) {
    clearSavedRequest(keyStore, MONEY_SCOPES.checkout, request.requestKey);
    return { kind: "finished", view };
  }
  // The intent id is saved before the caller can navigate away to pay.
  rememberServerId(keyStore, MONEY_SCOPES.checkout, request.requestKey, view.intentId);
  return { kind: "opened", view, paymentUrl: isOpenCheckout(view) ? safeRedirectUrl(view.paymentUrl, options.redirectPolicy) : null };
}

async function beginCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<CashOutAttempt> {
  const result = await settled(() => transport.cashOut.start({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits }));
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.cashOut, request, result.refusal);
  const view = result.value;
  if (!isRecord(view) || typeof view.id !== "string") return refused(keyStore, MONEY_SCOPES.cashOut, request, moneyRefusal("unconfirmed"));
  // OffRampService answers a same-key retry of a refused cash-out with the refused row and a 200: still a refusal.
  if (isRefusedStatus(view.status)) return refused(keyStore, MONEY_SCOPES.cashOut, request, moneyRefusal("definite", view.statusMessage));
  clearSavedRequest(keyStore, MONEY_SCOPES.cashOut, request.requestKey);
  return { kind: "accepted", view };
}

async function beginSale(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<SaleAttempt> {
  const sale = transport.sale;
  const quoteId = request.reference;
  if (!sale || quoteId === undefined) {
    // Nothing can be sent now, but an earlier send under this key may have been admitted: it stays saved until discarded.
    return { kind: "refused", refusal: moneyRefusal("definite", "This sale can no longer be sent."), kept: request };
  }
  const result = await settled(() => sale.sell({ requestKey: request.requestKey, quoteId }));
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.sale, request, result.refusal);
  const view = result.value;
  if (!isRecord(view) || typeof view.id !== "string") return refused(keyStore, MONEY_SCOPES.sale, request, moneyRefusal("unconfirmed"));
  if (isRefusedStatus(view.status)) return refused(keyStore, MONEY_SCOPES.sale, request, moneyRefusal("definite", view.statusMessage));
  clearSavedRequest(keyStore, MONEY_SCOPES.sale, request.requestKey);
  return { kind: "accepted", view };
}

type Claim = { kind: "ready"; request: SavedRequest } | PendingAttempt | RefusedAttempt;

/** A key that cannot be saved means the request never leaves: a definite refusal, nothing kept. */
function claimKey(keyStore: KeyStore, scope: string, amountMinorUnits: number, reference?: string): Claim {
  try {
    const claimed = claimRequestKey(keyStore, scope, amountMinorUnits, reference);
    return claimed.kind === "ready" ? claimed : { kind: "pending", pending: claimed.pending };
  } catch (error) {
    const message = error instanceof SdkError ? error.message : "The request could not be prepared, so it was not sent.";
    return { kind: "refused", refusal: moneyRefusal("definite", message), kept: null };
  }
}

function refused(keyStore: KeyStore, scope: string, request: SavedRequest, refusal: MoneyRefusal): RefusedAttempt {
  if (!releasesRequestKey(refusal)) return { kind: "refused", refusal, kept: request };
  clearSavedRequest(keyStore, scope, request.requestKey);
  return { kind: "refused", refusal, kept: null };
}

/** A transport call that cannot reject or resolve to junk: either is `unconfirmed`, because the request may have been admitted. */
async function settled<T>(run: () => Promise<MoneyResult<T>>): Promise<MoneyResult<T>> {
  try {
    const result = await run();
    if (isRecord(result) && result.ok === true && "value" in result) return result;
    if (isRecord(result) && result.ok === false && isRecord(result.refusal)) return result;
  } catch {
    // Falls through to unconfirmed.
  }
  return { ok: false, refusal: moneyRefusal("unconfirmed") };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isRefusedStatus(status: unknown): boolean {
  return typeof status === "string" && status.toLowerCase() === "refused";
}

interface WebLocks {
  request<T>(name: string, callback: () => Promise<T>): Promise<T>;
}

/** Holds a cross-tab Web Lock (`navigator.locks`) around claim-and-send, so two tabs never both mint a key; runs directly where there is none. */
async function withRequestLock<T>(scope: string, task: () => Promise<T>): Promise<T> {
  const locks = (globalThis as { navigator?: { locks?: WebLocks } }).navigator?.locks;
  if (!locks || typeof locks.request !== "function") return task();
  let started = false;
  try {
    return await locks.request(`azoa-sdk:money:${scope}`, () => {
      started = true;
      return task();
    });
  } catch (error) {
    // A runtime that refuses locks (opaque origin, sandboxed frame) must still send, exactly once.
    if (started) throw error;
    return task();
  }
}
