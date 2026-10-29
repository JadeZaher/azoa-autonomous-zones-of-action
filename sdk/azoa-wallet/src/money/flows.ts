// Framework-free keyed money flows: lock → claim → persist → send → resolve the key by the answer. See `src/money/AGENTS.md` §flows.
import { SdkError } from "../core/errors.js";
import { isOpenCheckout, isSettledCheckout, isTerminalCheckout, safeRedirectUrl } from "./presentation.js";
import type { RedirectPolicy } from "./presentation.js";
import { moneyRefusal, releasesRequestKey } from "./refusal.js";
import { MONEY_SCOPES, claimRequestKey, clearSavedRequest, markNeedsReview, readSavedRequest, rememberServerId } from "./request-key.js";
import type { KeyStore, SavedRequest } from "./request-key.js";
import type { AzoaMoneyTransport, CashOutView, CheckoutView, CheckoutVoidView, MoneyRefusal, MoneyResult, SaleQuoteView, SaleView } from "./types.js";

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

/**
 * `discarded`: the node voided the key, so nothing was or can be paid under it and it is forgotten.
 * `revealed`: a payment was or may be made under it; the key follows `view` (kept until it settles).
 * `refused`: the node could not decide (provider unreadable, busy, held for review); the key is `kept`.
 */
export type CheckoutDiscardAttempt =
  | SupersededAttempt
  | { kind: "discarded"; request: SavedRequest; intentId: string | null }
  | { kind: "revealed"; view: CheckoutView }
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
    return claimed.kind === "ready" ? beginCheckout(transport, keyStore, claimed.request, options, claimed.resent) : claimed;
  });
}

/** Resends a saved checkout with its original key, only while it is still the saved one; otherwise `superseded`. */
export function sendCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: CheckoutFlowOptions = {}): Promise<CheckoutAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.checkout, options, async () => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    return beginCheckout(transport, keyStore, current, options, true);
  });
}

/** Discards a saved checkout on the node's word only: voids its key first and forgets it only on `voided`. See `src/money/AGENTS.md` §void. */
export function discardCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: MoneyFlowOptions = {}): Promise<CheckoutDiscardAttempt> {
  return withRequestLock(keyStore, MONEY_SCOPES.checkout, options, async (): Promise<CheckoutDiscardAttempt> => {
    const current = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
    if (current?.requestKey !== request.requestKey) return { kind: "superseded", current };
    const decision = await voidOrReveal(transport, keyStore, current, options);
    if (decision.kind === "voided") return { kind: "discarded", request: current, intentId: decision.intentId };
    if (decision.kind === "refused") return decision;
    // Something was or may be paid: the key now follows that intent (kept until it settles), and no page is handed out.
    applyCheckoutView(keyStore, current, decision.view, options, false);
    return { kind: "revealed", view: decision.view };
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

/** `resend`: this key was sent before (a lost answer), so an earlier send may have been admitted under it. */
async function beginCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: CheckoutFlowOptions, resend: boolean): Promise<CheckoutAttempt> {
  const result = await settled(() => transport.checkout.begin({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits }), options);
  const view = result.ok ? asCheckoutView(result.value) : null;
  if (view) return applyCheckoutView(keyStore, request, view, options, true);
  // A key that already names an intent is decided by that intent's state, never by a refusal code (HIGH-1, LOW-1).
  if (request.serverId) return decideByState(transport, keyStore, request, request.serverId, options);
  const refusal = result.ok ? moneyRefusal("unconfirmed") : result.refusal;
  // A resent key's final-looking refusal (INTENT_FINISHED, REQUEST_KEY_IN_USE, a gate) is not proof for the earlier send: ask the node.
  if (resend && releasesRequestKey(refusal)) return releaseOnVoid(transport, keyStore, request, refusal, options);
  return refused(keyStore, MONEY_SCOPES.checkout, request, refusal);
}

/** Forgets the key only when the node voids it; a revealed payment is followed by its state and never navigated to. */
async function releaseOnVoid(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, refusal: MoneyRefusal, options: CheckoutFlowOptions): Promise<CheckoutAttempt> {
  const decision = await voidOrReveal(transport, keyStore, request, options);
  if (decision.kind === "voided") return { kind: "refused", refusal, kept: null };
  if (decision.kind === "revealed") return applyCheckoutView(keyStore, request, decision.view, options, false);
  return decision;
}

type VoidDecision = { kind: "voided"; intentId: string | null } | { kind: "revealed"; view: CheckoutView } | RefusedAttempt;

/** Void-or-reveal by request key: only an answer of `voided` clears the key; a refusal from the void keeps it, whatever its code. */
async function voidOrReveal(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest, options: MoneyFlowOptions): Promise<VoidDecision> {
  const result = await settled(() => transport.checkout.void(request.requestKey), options);
  const answer = result.ok ? asVoidAnswer(result.value) : null;
  if (answer?.outcome === "voided") {
    clearSavedRequest(keyStore, MONEY_SCOPES.checkout, request.requestKey);
    return { kind: "voided", intentId: answer.intentId };
  }
  if (answer?.outcome === "revealed") return { kind: "revealed", view: answer.intent };
  // A void that could not decide proves nothing, even when a host mapped it to a key-releasing code (e.g. `definite` for a 400).
  const refusal = !result.ok && !releasesRequestKey(result.refusal) ? result.refusal : moneyRefusal("unconfirmed");
  return refused(keyStore, MONEY_SCOPES.checkout, request, refusal);
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

/** `resent`: the claim reused a saved key, so this send repeats an earlier one. */
type Claim = { kind: "ready"; request: SavedRequest; resent: boolean } | PendingAttempt | RefusedAttempt;

/** A key that cannot be saved means the request never leaves: a definite refusal, nothing kept. */
function claimKey(keyStore: KeyStore, scope: string, amountMinorUnits: number, reference?: string): Claim {
  try {
    const before = readSavedRequest(keyStore, scope);
    const claimed = claimRequestKey(keyStore, scope, amountMinorUnits, reference);
    if (claimed.kind !== "ready") return { kind: "pending", pending: claimed.pending };
    return { kind: "ready", request: claimed.request, resent: before?.requestKey === claimed.request.requestKey };
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

/** A well-formed void answer, else null: host transports deserialize JSON, so only the literal outcomes count. */
function asVoidAnswer(value: unknown): CheckoutVoidView | null {
  if (!isRecord(value)) return null;
  if (value.outcome === "voided") return { outcome: "voided", intentId: typeof value.intentId === "string" ? value.intentId : null };
  const intent = asCheckoutView(value.intent);
  return value.outcome === "revealed" && intent ? { outcome: "revealed", intent } : null;
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
