// Runtime allowlist narrowing of node DTOs into browser-safe views, and the account gate. See `src/money/AGENTS.md` §views.
import type { FiatCheckoutIntent, TenantCustodialAccountStatus } from "../api/client.js";
import type {
  AccountCapabilitiesView,
  AccountGateState,
  BalanceView,
  CashOutQuoteView,
  CashOutView,
  CheckoutCapabilitiesView,
  CheckoutView,
  ConsentRevocationView,
  CustodialAccountView,
  KycSessionView,
  MoneyResult,
  PayoutDestinationView,
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

/** Copies only the browser-safe custodial account fields; readiness flags must be literally `true`. */
export function toCustodialAccountView(account: TenantCustodialAccountStatus | CustodialAccountView): CustodialAccountView {
  return {
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
  return {
    availableMinorUnits: balance.availableMinorUnits,
    onHoldMinorUnits: balance.onHoldMinorUnits,
    currencyCode: balance.currencyCode,
    availableFrom: balance.availableFrom ?? null,
  };
}

export function toSellableView(sellable: SellableView): SellableView {
  return {
    availableBaseUnits: sellable.availableBaseUnits,
    decimals: sellable.decimals,
    unitName: sellable.unitName,
    currencyCode: sellable.currencyCode,
    statusMessage: sellable.statusMessage,
  };
}

export function toSaleQuoteView(quote: SaleQuoteView): SaleQuoteView {
  return {
    quoteId: quote.quoteId,
    quantityBaseUnits: quote.quantityBaseUnits,
    proceedsMinorUnits: quote.proceedsMinorUnits,
    currencyCode: quote.currencyCode,
    expiresAt: quote.expiresAt ?? null,
  };
}

export function toSaleView(sale: SaleView): SaleView {
  return {
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
