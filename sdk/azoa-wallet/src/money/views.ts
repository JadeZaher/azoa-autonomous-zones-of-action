import { currencyMinorDigits } from "./presentation.js";
// Runtime allowlist narrowing of node DTOs into browser-safe views, and the account gate. See `src/money/AGENTS.md` §views.
import type { FiatCheckoutIntent, FiatCheckoutVoidResult, TenantCustodialAccountStatus } from "../api/client.js";
import type {
  AccountCapabilitiesView,
  AccountGateState,
  BalanceView,
  CashOutQuoteView,
  CashOutView,
  CheckoutCapabilitiesView,
  CheckoutView,
  CheckoutVoidView,
  ConsentRevocationView,
  CustodialAccountView,
  KycSessionView,
  MoneyResult,
  PayoutDestinationView,
  RefusalServerResult,
  SaleQuoteView,
  SaleView,
  SellableView,
} from "./types.js";

/** Keys that identify the subject, the tenant or a provider account and must never reach a browser. */
export const FORBIDDEN_VIEW_KEYS: readonly string[] = [
  "admissionKey", "tenantId", "externalSubject", "ardanovaUserId", "userId", "subjectUserId", "fundingTenantId",
  "connectedAccountId", "providerSessionId", "providerTransferId", "providerPayoutId", "missingConfigurationKeys",
];

/** Copies only the browser-safe checkout fields (an allowlist, so a new server field cannot leak by default). */
export function toCheckoutView(intent: FiatCheckoutIntent | CheckoutView): CheckoutView {
  return {
    intentId: intent.intentId,
    status: intent.status,
    settlementState: intent.settlementState,
    paymentUrl: intent.paymentUrl ?? null,
    resumed: intent.resumed === true,
    amountPaidMinorUnits: intent.amountPaidMinorUnits ?? null,
    currency: intent.currency ?? null,
    expiresAt: intent.expiresAt ?? null,
  };
}

/** Narrows a void answer: only the literal `voided` / `revealed` (with an intent) count, and `intent` is allowlist-narrowed; anything else throws. */
export function toCheckoutVoidView(answer: FiatCheckoutVoidResult | CheckoutVoidView): CheckoutVoidView {
  const raw = answer as { outcome?: unknown; intentId?: unknown; intent?: { intentId?: unknown } | null };
  if (raw.outcome === "voided") return { outcome: "voided", intentId: typeof raw.intentId === "string" ? raw.intentId : null };
  if (raw.outcome === "revealed" && typeof raw.intent?.intentId === "string") {
    return { outcome: "revealed", intent: toCheckoutView(raw.intent as FiatCheckoutIntent) };
  }
  throw new TypeError("Not a request-key void answer.");
}

/** Narrows a same-key 409's `result` (an intent projection, or the void's `{ outcome: "revealed", intent }`); null for anything else, never the raw object. */
export function toRefusalServerResult(result: unknown): RefusalServerResult | null {
  if (!isRecord(result)) return null;
  if (result.outcome === "revealed") return isCheckoutProjection(result.intent) ? { outcome: "revealed", intent: toCheckoutView(result.intent) } : null;
  return isCheckoutProjection(result) ? toCheckoutView(result) : null;
}

function isCheckoutProjection(value: unknown): value is FiatCheckoutIntent {
  return isRecord(value) && typeof value.intentId === "string" && typeof value.status === "string" && typeof value.settlementState === "string";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/** Copies only the browser-safe custodial account fields; readiness flags must be literally `true`. */
export function toCustodialAccountView(account: TenantCustodialAccountStatus | CustodialAccountView): CustodialAccountView {
  return {
    ownership: account.ownership === "claimed" ? "claimed" : account.ownership === "custodial" ? "custodial" : undefined,
    consent: account.consent === "granted" ? "granted" : "none",
    consentScopes: Array.isArray(account.consentScopes) ? account.consentScopes.filter((scope): scope is string => typeof scope === "string") : [],
    consentExpiresAt: account.consentExpiresAt === null ? null : typeof account.consentExpiresAt === "string" ? account.consentExpiresAt : undefined,
    walletId: account.walletId ?? null,
    walletAddress: account.walletAddress ?? null,
    kycStatus: account.kycStatus,
    identityReady: account.identityReady === true,
    kycReady: account.kycReady === true,
    walletReady: account.walletReady === true,
    ready: account.ready === true,
    unavailableReason: account.unavailableReason ?? null,
    reviewDueAt: account.reviewDueAt ?? null,
  };
}

export function toAccountCapabilitiesView(capabilities: AccountCapabilitiesView): AccountCapabilitiesView {
  return { ready: capabilities.ready === true, kycAvailable: capabilities.kycAvailable === true, unavailableReason: capabilities.unavailableReason ?? null };
}

export function toCheckoutCapabilitiesView(capabilities: CheckoutCapabilitiesView): CheckoutCapabilitiesView {
  return { available: capabilities.available === true, unavailableReason: capabilities.unavailableReason ?? null };
}

export function toKycSessionView(session: Partial<KycSessionView>): KycSessionView {
  return {
    verificationUrl: session.verificationUrl ?? null,
    expiresAt: session.expiresAt ?? null,
    instructions: session.instructions ?? null,
    developmentSimulation: session.developmentSimulation === true,
  };
}

export function toConsentRevocationView(receipt: ConsentRevocationView): ConsentRevocationView {
  return { revokedAt: receipt.revokedAt };
}

export function toCashOutQuoteView(quote: CashOutQuoteView): CashOutQuoteView {
  return {
    available: quote.available === true,
    accountReady: quote.accountReady === true,
    cashableMinorUnits: quote.cashableMinorUnits,
    onHoldMinorUnits: quote.onHoldMinorUnits,
    availableFrom: quote.availableFrom ?? null,
    minimumAmountMinorUnits: quote.minimumAmountMinorUnits,
    maximumAmountMinorUnits: quote.maximumAmountMinorUnits,
    currencyCode: quote.currencyCode,
    statusMessage: quote.statusMessage,
  };
}

export function toPayoutDestinationView(destination: PayoutDestinationView): PayoutDestinationView {
  return {
    available: destination.available === true,
    accountOpened: destination.accountOpened === true,
    readyToReceive: destination.readyToReceive === true,
    detailsSubmitted: destination.detailsSubmitted === true,
    outstandingRequirementCount: destination.outstandingRequirementCount,
    setupUrl: destination.setupUrl ?? null,
    setupUrlExpiresAt: destination.setupUrlExpiresAt ?? null,
    statusMessage: destination.statusMessage,
  };
}

export function toCashOutView(view: CashOutView): CashOutView {
  return {
    id: view.id,
    status: view.status,
    statusMessage: view.statusMessage,
    amountMinorUnits: view.amountMinorUnits,
    currencyCode: view.currencyCode,
    requestedAt: view.requestedAt,
    paidAt: view.paidAt ?? null,
    reversedAt: view.reversedAt ?? null,
  };
}

export function toBalanceView(balance: BalanceView): BalanceView {
  const denomination = narrowDenomination(balance.currencyCode, balance.currencyDecimals);
  return {
    ...denomination,
    availableMinorUnits: balance.availableMinorUnits,
    onHoldMinorUnits: balance.onHoldMinorUnits,
    currencyCode: balance.currencyCode,
    availableFrom: balance.availableFrom ?? null,
  };
}

export function toSellableView(sellable: SellableView): SellableView {
  const denomination = narrowDenomination(sellable.currencyCode, sellable.currencyDecimals);
  return {
    ...denomination,
    availableBaseUnits: sellable.availableBaseUnits,
    decimals: sellable.decimals,
    unitName: sellable.unitName,
    currencyCode: sellable.currencyCode,
    statusMessage: sellable.statusMessage,
  };
}

export function toSaleQuoteView(quote: SaleQuoteView): SaleQuoteView {
  if (!quote.quoteId?.trim() || !/^[1-9]\d*$/u.test(quote.quantityBaseUnits) || !Number.isSafeInteger(quote.proceedsMinorUnits) || quote.proceedsMinorUnits < 0 || currencyMinorDigits(quote.currencyCode, quote.currencyDecimals) === null) throw new TypeError("Invalid sale quote.");
  const denomination = narrowDenomination(quote.currencyCode, quote.currencyDecimals);
  return {
    ...denomination,
    quoteId: quote.quoteId,
    quantityBaseUnits: quote.quantityBaseUnits,
    proceedsMinorUnits: quote.proceedsMinorUnits,
    currencyCode: quote.currencyCode,
    expiresAt: quote.expiresAt ?? null,
  };
}

export function toSaleView(sale: SaleView): SaleView {
  const denomination = narrowDenomination(sale.currencyCode, sale.currencyDecimals);
  return {
    ...denomination,
    id: sale.id,
    status: sale.status,
    statusMessage: sale.statusMessage,
    quantityBaseUnits: sale.quantityBaseUnits,
    proceedsMinorUnits: sale.proceedsMinorUnits,
    currencyCode: sale.currencyCode,
    requestedAt: sale.requestedAt,
  };
}

/** Where an account read leaves the person: only `ready` may move value. */
export function deriveAccountGate(result: MoneyResult<CustodialAccountView>): AccountGateState {
  if (!result.ok) {
    switch (result.refusal.code) {
      case "consent_revoked": return "revoked";
      case "setup_required": return "setup";
      case "under_review": return "review";
      default: return "unavailable";
    }
  }
  const account = result.value;
  if (account.ownership === "claimed" && (account.consent !== "granted"
    || !Array.isArray(account.consentScopes) || !account.consentScopes.includes("kyc:read")
    || account.consentExpiresAt === undefined
    || (account.consentExpiresAt !== null && (!Number.isFinite(Date.parse(account.consentExpiresAt)) || Date.parse(account.consentExpiresAt) <= Date.now())))) return "revoked";
  if (account.ready === true) return "ready";
  if (account.kycStatus === "UnderReview") return "review";
  if (!account.identityReady || !account.kycReady) return "unavailable";
  return "setup";
}

/** Every forbidden key found anywhere in `value`, as dotted paths; empty means browser-safe. */
export function findForbiddenViewKeys(value: unknown, path = ""): string[] {
  if (value === null || typeof value !== "object") return [];
  if (Array.isArray(value)) return value.flatMap((item, index) => findForbiddenViewKeys(item, `${path}[${index}]`));
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) => {
    const here = path ? `${path}.${key}` : key;
    return [...(FORBIDDEN_VIEW_KEYS.includes(key) ? [here] : []), ...findForbiddenViewKeys(child, here)];
  });
}

function narrowDenomination(symbol: string, decimals: number | undefined): { currencyDecimals?: number } {
  if (decimals === undefined) return {};
  if (currencyMinorDigits(symbol, decimals) === null) throw new TypeError("Invalid host denomination.");
  return { currencyDecimals: decimals };
}
