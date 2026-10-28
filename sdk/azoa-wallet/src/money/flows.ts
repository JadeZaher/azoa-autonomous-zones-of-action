// Framework-free keyed money flows: lock → claim → persist → send → resolve the key by the answer. See `src/money/AGENTS.md` §flows.
import { SdkError } from "../core/errors.js";
import { isOpenCheckout, isSettledCheckout, isTerminalCheckout, safeRedirectUrl } from "./presentation.js";
import type { RedirectPolicy } from "./presentation.js";
import { moneyRefusal, releasesRequestKey } from "./refusal.js";
import { MONEY_SCOPES, claimRequestKey, clearSavedRequest, markNeedsReview, readSavedRequest, rememberServerId } from "./request-key.js";
import type { KeyStore, SavedRequest } from "./request-key.js";
import type { AzoaMoneyTransport, CashOutView, CheckoutView, MoneyRefusal, MoneyResult, SaleQuoteView, SaleView } from "./types.js";

/** A refused attempt; `kept` is the saved request whenever its key survives (resume it, never replace it). */
export type RefusedAttempt = { kind: "refused"; refusal: MoneyRefusal; kept: SavedRequest | null };

/** A different unresolved request is saved; it must be resumed or discarded first. */
export type PendingAttempt = { kind: "pending"; pending: SavedRequest };

/** The request being resent is no longer the saved one (another tab resolved or replaced it); nothing was sent. `current` is what is saved now. */
export type SupersededAttempt = { kind: "superseded"; current: SavedRequest | null };

/**
 * `opened`: the key now names this intent; pay at `paymentUrl` when it is set, otherwise watch the intent (it may be clearing).
 * `finished`: the intent is terminal, so nothing new can be charged; its key is kept until the money settles.
 */
export type CheckoutAttempt =
  | PendingAttempt
  | SupersededAttempt
  | { kind: "opened"; view: CheckoutView; paymentUrl: string | null }
  | { kind: "finished"; view: CheckoutView }
  | RefusedAttempt;

export type CashOutAttempt = PendingAttempt | SupersededAttempt | { kind: "accepted"; view: CashOutView } | RefusedAttempt;

export type SaleAttempt = PendingAttempt | SupersededAttempt | { kind: "accepted"; view: SaleView } | RefusedAttempt;

/** Bounds on one keyed send, so a hung transport can neither freeze this page nor other tabs. */
export interface MoneyFlowOptions {
  /** How long a transport call may take before it counts as `unconfirmed` (the key is kept); defaults to 45 000 ms. */
  sendTimeoutMs?: number;
  /** How long to wait for another tab's send before refusing with `rail_busy` (nothing claimed or sent here); defaults to 60 000 ms. */
  lockTimeoutMs?: number;
}

export interface CheckoutFlowOptions extends MoneyFlowOptions {
  /** Which payment pages `paymentUrl` may name; defaults to `DEFAULT_REDIRECT_ORIGINS`. */
  redirectPolicy?: RedirectPolicy;
}

const DEFAULT_SEND_TIMEOUT_MS = 45_000;
const DEFAULT_LOCK_TIMEOUT_MS = 60_000;
const LOCK_BUSY_MESSAGE = "Another window is still sending a request. Nothing was sent from here; try again shortly.";

/** Claims (or reuses) a checkout key, persists it, then begins the checkout; one tab at a time where Web Locks exist. */
export function startCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, amountMinorUnits: number, options: CheckoutFlowOptions = {}): Promise<CheckoutAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.checkout, options, async () => {
    const claimed = claimKey(keyStore, MONEY_SCOPES.checkout, amountMinorUnits);
    return claimed.kind === "ready" ? beginCheckout(transport, keyStore, claimed.request, options) : claimed;
  });
}

/** Resends a saved checkout with its original key, only while it is still the saved one; otherwise `superseded`. */
export function sendCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: CheckoutFlowOptions = {}): Promise<CheckoutAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.checkout, options, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginCheckout(transport, keyStore, current, options);
  });
}

/** Forgets the saved checkout key once `view` (its intent) is settled; true when it did. A terminal but unsettled intent keeps it. */
export function resolveCheckoutByState(keyStore: KeyStore, view: CheckoutView): boolean {
  if (!isSettledCheckout(view)) return false;
  const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
  if (saved?.serverId !== view.intentId) return false;
  clearSavedRequest(keyStore, MONEY_SCOPES.checkout, saved.requestKey);
  return true;
}

/** Claims (or reuses) a cash-out key, persists it, then starts the cash-out. */
export function startCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, amountMinorUnits: number, options: MoneyFlowOptions = {}): Promise<CashOutAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.cashOut, options, async () => {
    const claimed = claimKey(keyStore, MONEY_SCOPES.cashOut, amountMinorUnits);
    return claimed.kind === "ready" ? beginCashOut(transport, keyStore, claimed.request, options) : claimed;
  });
}

/** Resends a saved cash-out with its original key, only while it is still the saved one. */
export function sendCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: MoneyFlowOptions = {}): Promise<CashOutAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.cashOut, options, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.cashOut);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginCashOut(transport, keyStore, current, options);
  });
}

/** Claims a sale key bound to the quote (its id is replayed on resume), persists it, then sells. */
export function startSale(transport: AzoaMoneyTransport, keyStore: KeyStore, quote: SaleQuoteView, options: MoneyFlowOptions = {}): Promise<SaleAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.sale, options, async () => {
    const claimed = claimKey(keyStore, MONEY_SCOPES.sale, quote.proceedsMinorUnits, quote.quoteId);
    return claimed.kind === "ready" ? beginSale(transport, keyStore, claimed.request, options) : claimed;
  });
}

/** Resends a saved sale (same key, same quote id), only while it is still the saved one. */
export function sendSale(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: MoneyFlowOptions = {}): Promise<SaleAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.sale, options, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.sale);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginSale(transport, keyStore, current, options);
  });
}

async function beginCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: CheckoutFlowOptions): Promise<CheckoutAttempt> {
  const result = await settled(() => transport.checkout.begin({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits }), options);
  const view = result.ok ? asCheckoutView(result.value) : null;
  if (view) return applyCheckoutView(keyStore, request, view, options, true);
  // A key that already names an intent is decided by that intent's state, never by a refusal code (HIGH-1, LOW-1).
  if (request.serverId) return decideByState(transport, keyStore, request, request.serverId, options);
  return refused(keyStore, MONEY_SCOPES.checkout, request, result.ok ? moneyRefusal("unconfirmed") : result.refusal);
}

/** Reads the key's intent and lets the key go only on a settled view; anything else keeps it and shows the real state. */
async function decideByState(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, intentId: string, options: CheckoutFlowOptions): Promise<CheckoutAttempt> {
  const read = await settled(() => transport.checkout.get(intentId), options);
  const view = read.ok ? asCheckoutView(read.value) : null;
  // The refused send never hands out a payment page by itself; an open one is shown, not navigated to.
  if (view?.intentId === intentId) return applyCheckoutView(keyStore, request, view, options, false);
  return { kind: "refused", refusal: read.ok ? moneyRefusal("unconfirmed") : read.refusal, kept: request };
}

function applyCheckoutView(keyStore: KeyStore, request: SavedRequest, view: CheckoutView, options: CheckoutFlowOptions, redirect: boolean): CheckoutAttempt {
  if (isSettledCheckout(view)) {
    clearSavedRequest(keyStore, MONEY_SCOPES.checkout, request.requestKey);
    return { kind: "finished", view };
  }
  // The intent id is saved before the caller can navigate away to pay; the key stays until the money settles.
  rememberServerId(keyStore, MONEY_SCOPES.checkout, request.requestKey, view.intentId);
  if (isTerminalCheckout(view)) return { kind: "finished", view };
  const paymentUrl = redirect && isOpenCheckout(view) ? safeRedirectUrl(view.paymentUrl, options.redirectPolicy) : null;
  return { kind: "opened", view, paymentUrl };
}

async function beginCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: MoneyFlowOptions): Promise<CashOutAttempt> {
  const result = await settled(() => transport.cashOut.start({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits }), options);
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.cashOut, request, result.refusal);
  const view = result.value;
  if (!isRecord(view) || typeof view.id !== "string") return refused(keyStore, MONEY_SCOPES.cashOut, request, moneyRefusal("unconfirmed"));
  // OffRampService answers a same-key retry of a refused cash-out with the refused row and a 200: still a refusal.
  if (isRefusedStatus(view.status)) return refused(keyStore, MONEY_SCOPES.cashOut, request, moneyRefusal("definite", view.statusMessage));
  clearSavedRequest(keyStore, MONEY_SCOPES.cashOut, request.requestKey);
  return { kind: "accepted", view };
}

async function beginSale(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: MoneyFlowOptions): Promise<SaleAttempt> {
  const sale = transport.sale;
  const quoteId = request.reference;
  if (!sale || quoteId === undefined) {
    // Nothing can be sent now, but an earlier send under this key may have been admitted: it stays saved until discarded.
    return { kind: "refused", refusal: moneyRefusal("definite", "This sale can no longer be sent."), kept: request };
  }
  const result = await settled(() => sale.sell({ requestKey: request.requestKey, quoteId }), options);
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
  if (!releasesRequestKey(refusal)) {
    // Held for an operator: later visits must show that, not offer it as a lost answer to resume.
    if (refusal.code === "needs_review") markNeedsReview(keyStore, scope, request.requestKey);
    const saved = readSavedRequest(keyStore, scope);
    return { kind: "refused", refusal, kept: saved?.requestKey === request.requestKey ? saved : request };
  }
  clearSavedRequest(keyStore, scope, request.requestKey);
  return { kind: "refused", refusal, kept: null };
}

/** A transport call that cannot reject, resolve to junk or hang: each is `unconfirmed`, because the request may have been admitted. */
async function settled<T>(run: () => Promise<MoneyResult<T>>, options: MoneyFlowOptions): Promise<MoneyResult<T>> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const timedOut = new Promise<null>(resolve => {
      timer = setTimeout(() => resolve(null), options.sendTimeoutMs ?? DEFAULT_SEND_TIMEOUT_MS);
    });
    const result: unknown = await Promise.race([run(), timedOut]);
    if (isRecord(result) && result.ok === true && "value" in result) return result as MoneyResult<T>;
    if (isRecord(result) && result.ok === false && isRecord(result.refusal)) return result as MoneyResult<T>;
  } catch {
    // Falls through to unconfirmed.
  } finally {
    clearTimeout(timer);
  }
  return { ok: false, refusal: moneyRefusal("unconfirmed") };
}

function asCheckoutView(value: unknown): CheckoutView | null {
  return isRecord(value) && typeof value.intentId === "string" ? (value as unknown as CheckoutView) : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isRefusedStatus(status: unknown): boolean {
  return typeof status === "string" && status.toLowerCase() === "refused";
}

interface WebLocks {
  request<T>(name: string, options: { signal?: AbortSignal }, callback: () => Promise<T>): Promise<T>;
}

/** Holds a cross-tab Web Lock (`navigator.locks`) around claim-and-send, waiting at most `lockTimeoutMs`; runs directly where there is none. */
async function withRequestLock<T>(keyStore: KeyStore, scope: string, options: MoneyFlowOptions, task: () => Promise<T>): Promise<T | RefusedAttempt> {
  const locks = (globalThis as { navigator?: { locks?: WebLocks } }).navigator?.locks;
  if (!locks || typeof locks.request !== "function") return task();
  const wait = timeoutSignal(options.lockTimeoutMs ?? DEFAULT_LOCK_TIMEOUT_MS);
  let started = false;
  try {
    return await locks.request(`azoa-sdk:money:${scope}`, { signal: wait.signal }, () => {
      started = true;
      return task();
    });
  } catch (error) {
    if (started) throw error;
    // Another tab held the lock past the wait (a hung send there): nothing was claimed or sent here.
    if (wait.signal.aborted) return { kind: "refused", refusal: moneyRefusal("rail_busy", LOCK_BUSY_MESSAGE), kept: readSavedRequest(keyStore, scope) };
    // A runtime that refuses locks (opaque origin, sandboxed frame) must still send, exactly once.
    return task();
  } finally {
    wait.clear();
  }
}

/** `AbortSignal.timeout` where the runtime has it, else an `AbortController` on a timer. */
function timeoutSignal(ms: number): { signal: AbortSignal; clear(): void } {
  if (typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function") return { signal: AbortSignal.timeout(ms), clear: () => undefined };
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), ms);
  return { signal: controller.signal, clear: () => clearTimeout(timer) };
}
