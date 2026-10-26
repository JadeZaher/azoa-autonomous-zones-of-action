// Framework-free keyed money flows: claim → persist → send → resolve the key by the answer. See `src/money/AGENTS.md` §flows.
import { SdkError } from "../core/errors.js";
import { isFinishedCheckout, safeRedirectUrl } from "./presentation.js";
import { releasesRequestKey } from "./refusal.js";
import { MONEY_SCOPES, claimRequestKey, clearSavedRequest, readSavedRequest, rememberServerId } from "./request-key.js";
import type { KeyStore, SavedRequest } from "./request-key.js";
import type { AzoaMoneyTransport, CashOutView, CheckoutView, MoneyRefusal, SaleQuoteView, SaleView } from "./types.js";

/** A refused attempt; `kept` is the saved request when the refusal may hide an admission (resume it, never replace it). */
export type RefusedAttempt = { kind: "refused"; refusal: MoneyRefusal; kept: SavedRequest | null };

/** A different unresolved request is saved; it must be resumed or discarded first. */
export type PendingAttempt = { kind: "pending"; pending: SavedRequest };

/** `opened`: pay at `paymentUrl` (the key now remembers the intent id). `finished`: the key's intent already ended. */
export type CheckoutAttempt =
  | PendingAttempt
  | { kind: "opened"; view: CheckoutView; paymentUrl: string | null }
  | { kind: "finished"; view: CheckoutView }
  | RefusedAttempt;

export type CashOutAttempt = PendingAttempt | { kind: "accepted"; view: CashOutView } | RefusedAttempt;

export type SaleAttempt = PendingAttempt | { kind: "accepted"; view: SaleView } | RefusedAttempt;

/** Claims (or reuses) a checkout key, persists it, then begins the checkout. */
export async function startCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, amountMinorUnits: number): Promise<CheckoutAttempt> {
  const claimed = claimKey(keyStore, MONEY_SCOPES.checkout, amountMinorUnits);
  return claimed.kind === "ready" ? sendCheckout(transport, keyStore, claimed.request) : claimed;
}

/** Sends a saved checkout request with its original key and resolves the key by the answer. */
export async function sendCheckout(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<CheckoutAttempt> {
  const result = await transport.checkout.begin({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits });
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.checkout, request, result.refusal);
  if (isFinishedCheckout(result.value)) {
    clearSavedRequest(keyStore, MONEY_SCOPES.checkout, request.requestKey);
    return { kind: "finished", view: result.value };
  }
  // The intent id is saved before the caller can navigate away to pay.
  rememberServerId(keyStore, MONEY_SCOPES.checkout, request.requestKey, result.value.intentId);
  return { kind: "opened", view: result.value, paymentUrl: safeRedirectUrl(result.value.paymentUrl) };
}

/** Forgets the saved checkout key when `view` is its finished intent; true when it did. */
export function resolveCheckoutByState(keyStore: KeyStore, view: CheckoutView): boolean {
  if (!isFinishedCheckout(view)) return false;
  const saved = readSavedRequest(keyStore, MONEY_SCOPES.checkout);
  if (saved?.serverId !== view.intentId) return false;
  clearSavedRequest(keyStore, MONEY_SCOPES.checkout, saved.requestKey);
  return true;
}

/** Claims (or reuses) a cash-out key, persists it, then starts the cash-out. */
export async function startCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, amountMinorUnits: number): Promise<CashOutAttempt> {
  const claimed = claimKey(keyStore, MONEY_SCOPES.cashOut, amountMinorUnits);
  return claimed.kind === "ready" ? sendCashOut(transport, keyStore, claimed.request) : claimed;
}

export async function sendCashOut(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<CashOutAttempt> {
  const result = await transport.cashOut.start({ requestKey: request.requestKey, amountMinorUnits: request.amountMinorUnits });
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.cashOut, request, result.refusal);
  clearSavedRequest(keyStore, MONEY_SCOPES.cashOut, request.requestKey);
  return { kind: "accepted", view: result.value };
}

/** Claims a sale key bound to the quote (its id is replayed on resume), persists it, then sells. */
export async function startSale(transport: AzoaMoneyTransport, keyStore: KeyStore, quote: SaleQuoteView): Promise<SaleAttempt> {
  const claimed = claimKey(keyStore, MONEY_SCOPES.sale, quote.proceedsMinorUnits, quote.quoteId);
  return claimed.kind === "ready" ? sendSale(transport, keyStore, claimed.request) : claimed;
}

export async function sendSale(transport: AzoaMoneyTransport, keyStore: KeyStore, request: SavedRequest): Promise<SaleAttempt> {
  const sale = transport.sale;
  if (!sale || request.reference === undefined) {
    return { kind: "refused", refusal: { code: "definite", message: "This sale can no longer be sent." }, kept: null };
  }
  const result = await sale.sell({ requestKey: request.requestKey, quoteId: request.reference });
  if (!result.ok) return refused(keyStore, MONEY_SCOPES.sale, request, result.refusal);
  clearSavedRequest(keyStore, MONEY_SCOPES.sale, request.requestKey);
  return { kind: "accepted", view: result.value };
}

type Claim = { kind: "ready"; request: SavedRequest } | PendingAttempt | RefusedAttempt;

/** A key that cannot be saved means the request never leaves: a definite refusal, nothing kept. */
function claimKey(keyStore: KeyStore, scope: string, amountMinorUnits: number, reference?: string): Claim {
  try {
    const claimed = claimRequestKey(keyStore, scope, amountMinorUnits, reference);
    return claimed.kind === "ready" ? claimed : { kind: "pending", pending: claimed.pending };
  } catch (error) {
    const message = error instanceof SdkError ? error.message : "The request could not be prepared, so it was not sent.";
    return { kind: "refused", refusal: { code: "definite", message }, kept: null };
  }
}

function refused(keyStore: KeyStore, scope: string, request: SavedRequest, refusal: MoneyRefusal): RefusedAttempt {
  if (!releasesRequestKey(refusal)) return { kind: "refused", refusal, kept: request };
  clearSavedRequest(keyStore, scope, request.requestKey);
  return { kind: "refused", refusal, kept: null };
}
