// Money surface contracts: refusals, browser-safe views and the host-injected transport. See `src/money/AGENTS.md`.
import type {
  CashOutQuote,
  CashOutView as NodeCashOutView,
  FiatCheckoutIntent,
  HostedSessionCapabilities,
  PayoutAccountStatus,
  TenantCustodialAccountStatus,
  TenantCustodialCapabilities,
  TenantKycSession,
} from "../api/client.js";
import type { ConsentRevocationReceipt } from "../api/consent.js";

/** Closed refusal vocabulary every transport maps node and host failures into. See `src/money/AGENTS.md` §refusals. */
export type MoneyRefusalCode =
  | "setup_required"
  | "under_review"
  | "consent_revoked"
  | "account_holder_required"
  | "kyb_required"
  | "payments_not_configured"
  | "rail_busy"
  | "definite"
  | "unconfirmed"
  | "needs_review"
  | "unauthenticated";

/** Why a money call did not happen; `message` is one sentence for a person, never rail or credential detail. */
export interface MoneyRefusal {
  code: MoneyRefusalCode;
  message: string;
  retryAfterSeconds?: number;
}

/** Every transport method resolves to this; a transport never throws for a refusal. */
export type MoneyResult<T> = { ok: true; value: T } | { ok: false; refusal: MoneyRefusal };

/** A checkout as the browser may see it: `admissionKey` embeds the subject and tenant ids, so it stays server-side. */
export type CheckoutView = Omit<FiatCheckoutIntent, "admissionKey">;

/**
 * The node's answer to voiding a checkout request key. `voided`: nothing was or can be paid under it, so it may be
 * forgotten. `revealed`: a payment was or may be made under it; keep the key and follow `intent`. See `src/money/AGENTS.md` §void.
 */
export type CheckoutVoidView =
  | { outcome: "voided"; intentId: string | null }
  | { outcome: "revealed"; intent: CheckoutView };

/** The custodial account as the browser may see it: no tenant, subject or AZOA user ids. */
export type CustodialAccountView = Omit<TenantCustodialAccountStatus, "tenantId" | "externalSubject" | "ardanovaUserId" | "userId">;

/** Whether the node can onboard an account right now. */
export type AccountCapabilitiesView = Pick<TenantCustodialCapabilities, "ready" | "kycAvailable" | "unavailableReason">;

/** Whether the node can take a payment right now; missing configuration key names stay in the operator console. */
export type CheckoutCapabilitiesView = Pick<HostedSessionCapabilities, "available" | "unavailableReason">;

/** Where to send the person to verify their identity. */
export type KycSessionView = Pick<TenantKycSession, "verificationUrl" | "expiresAt" | "instructions" | "developmentSimulation">;

/** Confirmation that the host may no longer act for this person. */
export type ConsentRevocationView = Pick<ConsentRevocationReceipt, "revokedAt">;

/** What the person may cash out today, in money. */
export type CashOutQuoteView = CashOutQuote;

/** Where the person's cash-outs go and whether it can receive money. */
export type PayoutDestinationView = PayoutAccountStatus;

/** One cash-out as the person sees it (already free of chain words and provider ids). */
export type CashOutView = NodeCashOutView;

/** The person's money balance; amounts are integer minor units of `currencyCode`. */
export interface BalanceView {
  /** What may leave today. */
  availableMinorUnits: number;
  /** What is still inside a reversal window. */
  onHoldMinorUnits: number;
  currencyCode: string;
  /** When the oldest held part frees up, when knowable. */
  availableFrom: string | null;
}

/** Units the person may sell to the host; quantities are base-unit decimal strings, never JavaScript numbers. */
export interface SellableView {
  availableBaseUnits: string;
  /** Decimal places between a base unit and one whole unit. */
  decimals: number;
  unitName: string;
  currencyCode: string;
  statusMessage: string;
}

export interface SaleQuoteRequest {
  quantityBaseUnits: string;
}

/** The host's price for one sale; `quoteId` pins quantity and proceeds. */
export interface SaleQuoteView {
  quoteId: string;
  quantityBaseUnits: string;
  proceedsMinorUnits: number;
  currencyCode: string;
  expiresAt: string | null;
}

export interface SaleRequest {
  requestKey: string;
  quoteId: string;
}

/** One accepted sale as the person sees it. */
export interface SaleView {
  id: string;
  status: string;
  statusMessage: string;
  quantityBaseUnits: string;
  proceedsMinorUnits: number;
  currencyCode: string;
  requestedAt: string;
}

/** A keyed money request: the same key resumes the same request instead of starting a second one. */
export interface MoneyRequest {
  requestKey: string;
  amountMinorUnits: number;
}

export interface AccountTransport {
  capabilities(): Promise<MoneyResult<AccountCapabilitiesView>>;
  status(): Promise<MoneyResult<CustodialAccountView>>;
  /** Creates or resumes the account for the session's subject; idempotent. */
  ensure(): Promise<MoneyResult<CustodialAccountView>>;
  beginKyc(): Promise<MoneyResult<KycSessionView>>;
  revokeConsent(): Promise<MoneyResult<ConsentRevocationView>>;
}

export interface CheckoutTransport {
  capabilities(): Promise<MoneyResult<CheckoutCapabilitiesView>>;
  begin(request: MoneyRequest): Promise<MoneyResult<CheckoutView>>;
  get(intentId: string): Promise<MoneyResult<CheckoutView>>;
  /** Voids `requestKey` (later begins under it are refused) or reveals the payment made under it; idempotent. Any refusal means keep the key. */
  void(requestKey: string): Promise<MoneyResult<CheckoutVoidView>>;
}

export interface CashOutTransport {
  availability(): Promise<MoneyResult<CashOutQuoteView>>;
  destination(): Promise<MoneyResult<PayoutDestinationView>>;
  beginDestinationSetup(): Promise<MoneyResult<PayoutDestinationView>>;
  start(request: MoneyRequest): Promise<MoneyResult<CashOutView>>;
  /** Newest first. */
  history(): Promise<MoneyResult<CashOutView[]>>;
}

/** Host-priced sale of units back to money; optional because only some hosts buy units. */
export interface SaleTransport {
  sellable(): Promise<MoneyResult<SellableView>>;
  quote(request: SaleQuoteRequest): Promise<MoneyResult<SaleQuoteView>>;
  sell(request: SaleRequest): Promise<MoneyResult<SaleView>>;
}

export interface BalanceTransport {
  get(): Promise<MoneyResult<BalanceView>>;
}

/** The host-injected seam for every privileged money action. The subject is the host session, never a parameter. */
export interface AzoaMoneyTransport {
  account: AccountTransport;
  checkout: CheckoutTransport;
  cashOut: CashOutTransport;
  sale?: SaleTransport;
  balance: BalanceTransport;
}

/** Where the account stands for money: `ready` is the only state that may move value. */
export type AccountGateState = "ready" | "setup" | "review" | "revoked" | "unavailable";

/** What a redirect is for, so the host can route it. */
export type MoneyRedirectKind = "checkout" | "kyc" | "payout-setup";
