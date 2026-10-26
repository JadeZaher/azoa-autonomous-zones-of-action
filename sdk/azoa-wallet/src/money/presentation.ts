// Amount parsing/formatting, checkout state predicates and the default copy map. See `src/money/AGENTS.md` §presentation.
import type { FiatSettlementState, TenantKycStatus } from "../api/client.js";
import type { AccountGateState, CheckoutView, MoneyRefusal, MoneyRefusalCode } from "./types.js";

/** A checkout intent the key can no longer usefully resume: paid, failed or expired. */
export function isFinishedCheckout(intent: { status: string; settlementState: string }): boolean {
  return ["complete", "failed", "expired"].includes(intent.status)
    || intent.settlementState === "funds_applied" || intent.settlementState === "payment_failed";
}

/** An HTTP refusal that is a definite answer for a request key (400/403/404/422); anything else may have been admitted. */
export function isDefiniteRefusal(status: number | undefined): boolean {
  return status === 400 || status === 403 || status === 404 || status === 422;
}

/** Parses a decimal amount in major units (e.g. "12.50") into integer minor units; null when not exact. */
export function toMinorUnits(text: string): number | null {
  const match = /^(\d{1,9})(?:\.(\d{1,2}))?$/.exec(text.trim());
  if (!match) return null;
  return Number(match[1]) * 100 + Number((match[2] ?? "").padEnd(2, "0"));
}

/** Formats integer minor units for display only. */
export function formatMinorUnits(minorUnits: number, currencyCode: string): string {
  const sign = minorUnits < 0 ? "-" : "";
  const absolute = Math.abs(minorUnits);
  return `${sign}${Math.floor(absolute / 100).toLocaleString()}.${String(absolute % 100).padStart(2, "0")} ${currencyCode.toUpperCase()}`.trim();
}

/** Parses a decimal unit quantity into a base-unit decimal string with exact BigInt math; null when not exact. */
export function toBaseUnits(text: string, decimals: number): string | null {
  if (!Number.isInteger(decimals) || decimals < 0 || decimals > 30) return null;
  const match = /^(\d{1,30})(?:\.(\d+))?$/.exec(text.trim());
  if (!match) return null;
  const fraction = match[2] ?? "";
  if (fraction.length > decimals) return null;
  return (BigInt(match[1]!) * 10n ** BigInt(decimals) + BigInt(fraction.padEnd(decimals, "0") || "0")).toString();
}

/** Formats a base-unit decimal string as whole units for display, without trailing fraction zeros. */
export function formatBaseUnits(baseUnits: string, decimals: number): string {
  if (!/^\d+$/.test(baseUnits)) return baseUnits;
  const value = BigInt(baseUnits);
  const scale = 10n ** BigInt(decimals);
  const fraction = (value % scale).toString().padStart(decimals, "0").replace(/0+$/, "");
  return `${(value / scale).toLocaleString()}${fraction ? `.${fraction}` : ""}`;
}

/** Only `https:` (and `http:` on loopback, for development) may be handed to the host as a redirect or link. */
export function safeRedirectUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url);
    if (parsed.protocol === "https:") return url;
    if (parsed.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(parsed.hostname)) return url;
  } catch {
    // Not an absolute URL.
  }
  return null;
}

/** Every sentence the money UI shows; hosts override any part through `messages`. */
export interface MoneyMessages {
  settlement: Record<FiatSettlementState, string>;
  refusal: Partial<Record<MoneyRefusalCode, string>>;
  gate: Record<AccountGateState | "loading", string> & { reviewDue: (date: string) => string };
  kycStatus: Record<TenantKycStatus, string>;
  kycStatusLabel: Record<TenantKycStatus, string>;
  kyc: { title: string; start: string; continue: string; finishSetup: string; checkAgain: string; openVerification: string; simulation: string };
  consent: { title: string; active: string; revoked: string; revoke: string; confirmRevoke: string; keep: string; confirmPrompt: string };
  account: { title: string; identityLabel: string; walletLabel: string; walletReady: string; walletPending: string; addressLabel: string };
  addFunds: {
    title: string; description: string; amountLabel: string; submit: string; unavailable: string;
    invalidAmount: string; outOfRange: (minimum: string, maximum: string) => string; resumedFinished: string;
  };
  checkout: { title: string; loading: string; expired: string; openPaymentPage: string; check: string; paid: (amount: string) => string; expires: (date: string) => string };
  pending: {
    notConfirmed: (summary: string) => string;
    summary: Record<PendingKind, (amount: string) => string>;
    detail: Record<PendingKind, string>;
    resume: Record<PendingKind, string>;
    discard: string;
    discarded: string;
  };
  cashOut: {
    title: string; amountLabel: string; submit: string; cashableLabel: string; onHoldLabel: string; availableFromLabel: string;
    outOfRange: (minimum: string, maximum: string) => string; historyTitle: string; historyEmpty: string; notReady: string;
  };
  destination: {
    title: string; start: string; continue: string; openSetupPage: string; outstanding: (count: number) => string;
    accountHolderOnly: string; accountHolderAction: string;
  };
  sale: {
    title: string; quantityLabel: string; availableLabel: string; getQuote: string; submit: string;
    quoteSummary: (quantity: string, proceeds: string) => string; quoteExpires: (date: string) => string; invalidQuantity: (available: string) => string;
  };
  balance: { title: string; availableLabel: string; onHoldLabel: string; availableFrom: (date: string) => string };
  common: { refresh: string; retryAfter: (seconds: number) => string; genericRefusal: string };
}

/** Which kind of saved request a pending notice describes. */
export type PendingKind = "checkout" | "cashOut" | "sale";

/** Section-wise partial override of {@link MoneyMessages}. */
export type MoneyMessagesOverride = { [Section in keyof MoneyMessages]?: Partial<MoneyMessages[Section]> };

/** The SDK's default copy (English). */
export const defaultMoneyMessages: MoneyMessages = {
  settlement: {
    not_started: "Waiting for your payment.",
    payment_received: "Your payment cleared. The funds are being added to your account.",
    funds_applied: "The funds are in your account.",
    payment_failed: "The payment did not go through. Nothing was added.",
  },
  refusal: {
    setup_required: "Finish verifying your identity first. Nothing was moved.",
    consent_revoked: "This app no longer has your permission to manage payments for your account. Nothing was moved.",
    account_holder_required: "Only you can set up where your money is paid out, signed in to your AZOA account directly. This app cannot do it for you.",
    kyb_required: "Payments are paused while this app renews its business verification. Nothing was moved.",
    payments_not_configured: "Payments are not set up here yet, so nothing can be charged. Nothing was charged.",
    rail_busy: "The payment service is busy. Nothing was moved; try again shortly.",
    unconfirmed: "The answer did not arrive, so it is not known whether this went through. Resuming it cannot charge or pay twice.",
    unauthenticated: "Your session expired. Sign in again.",
  },
  gate: {
    loading: "Checking your account…",
    ready: "Your account is ready for payments.",
    setup: "Finish setting up your account to add or cash out money.",
    review: "Your identity is verified and an additional compliance check is under review. Nothing more is needed from you; payments open once it is resolved.",
    revoked: "You withdrew this app's permission to manage your payments, so nothing can be added or cashed out here.",
    unavailable: "Payments are not available for your account right now.",
    reviewDue: date => `Expected by ${date}.`,
  },
  kycStatus: {
    Unknown: "Before you can add or cash out money, verify your identity. It takes a few minutes.",
    Pending: "Your verification has started. Continue where you left off.",
    Approved: "Your identity is verified.",
    Rejected: "Your verification was not approved. You can try again.",
    UnderReview: "Your identity is verified and an additional compliance check is under review. Nothing more is needed from you.",
  },
  kycStatusLabel: { Unknown: "Not started", Pending: "In progress", Approved: "Verified", Rejected: "Not approved", UnderReview: "Under review" },
  kyc: {
    title: "Verify your identity",
    start: "Verify your identity",
    continue: "Continue verification",
    finishSetup: "Finish setting up your account",
    checkAgain: "I finished — check again",
    openVerification: "Open the verification page",
    simulation: "This node simulates verification for development.",
  },
  consent: {
    title: "Payment permission",
    active: "This app may manage payments for your account.",
    revoked: "You withdrew this app's permission to manage your payments. Nothing can be added or cashed out here until it is granted again.",
    revoke: "Withdraw permission",
    confirmRevoke: "Yes, withdraw permission",
    keep: "Keep it",
    confirmPrompt: "Withdrawing stops this app from adding or cashing out money for you. Money already in your account stays there.",
  },
  account: { title: "Your account", identityLabel: "Identity", walletLabel: "Wallet", walletReady: "Ready", walletPending: "Being set up", addressLabel: "Address" },
  addFunds: {
    title: "Add funds",
    description: "Pay by card. Your balance changes only after the payment provider confirms the money arrived.",
    amountLabel: "Amount",
    submit: "Continue to payment",
    unavailable: "Payments are not available right now.",
    invalidAmount: "Enter an amount like 25 or 25.50.",
    outOfRange: (minimum, maximum) => `Enter an amount between ${minimum} and ${maximum}.`,
    resumedFinished: "This matched your earlier payment, which has already finished. Nothing new was charged.",
  },
  checkout: {
    title: "Payment",
    loading: "Reading the payment…",
    expired: "The payment page expired. Nothing was charged.",
    openPaymentPage: "Continue to the payment page",
    check: "Check payment",
    paid: amount => `Paid: ${amount}`,
    expires: date => `The payment page works until ${date}.`,
  },
  pending: {
    notConfirmed: summary => `${summary} has not been confirmed yet.`,
    summary: {
      checkout: amount => `A payment of ${amount}`,
      cashOut: amount => `A cash-out of ${amount}`,
      sale: amount => `A sale for ${amount}`,
    },
    detail: {
      checkout: "Resuming reopens that same payment instead of starting a second one. Discard it only if you want a separate payment; a payment page you already completed still counts.",
      cashOut: "Resuming sends the same request again, and it is paid at most once. If it is already in the history, it was received. Discard it only if you want a separate cash-out.",
      sale: "Resuming sends the same sale again; it completes at most once. Discard it only if you want a separate sale.",
    },
    resume: { checkout: "Resume that payment", cashOut: "Resume that cash-out", sale: "Resume that sale" },
    discard: "Discard it and start a new request",
    discarded: "The unconfirmed request was discarded. Enter an amount to start a new one.",
  },
  cashOut: {
    title: "Cash out",
    amountLabel: "Amount",
    submit: "Cash out",
    cashableLabel: "Cashable now",
    onHoldLabel: "Still on hold",
    availableFromLabel: "Held part frees up",
    outOfRange: (minimum, maximum) => `Enter an amount between ${minimum} and ${maximum}.`,
    historyTitle: "History",
    historyEmpty: "No cash-outs yet.",
    notReady: "Set up your payout account before cashing out.",
  },
  destination: {
    title: "Payout account",
    start: "Set up payout account",
    continue: "Continue setup",
    openSetupPage: "Open the setup page",
    outstanding: count => `${count} item(s) still needed by the provider.`,
    accountHolderOnly: "Only you can set up where your money is paid out, signed in to your AZOA account directly. This app cannot do it for you.",
    accountHolderAction: "Set it up in AZOA",
  },
  sale: {
    title: "Sell units",
    quantityLabel: "Quantity",
    availableLabel: "Available to sell",
    getQuote: "Get a price",
    submit: "Sell",
    quoteSummary: (quantity, proceeds) => `Sell ${quantity} for ${proceeds}.`,
    quoteExpires: date => `This price holds until ${date}.`,
    invalidQuantity: available => `Enter a quantity up to ${available}.`,
  },
  balance: {
    title: "Balance",
    availableLabel: "Available",
    onHoldLabel: "On hold",
    availableFrom: date => `Held funds free up on ${date}.`,
  },
  common: {
    refresh: "Refresh",
    retryAfter: seconds => `Try again in ${seconds} seconds.`,
    genericRefusal: "That did not go through. Nothing was moved.",
  },
};

/** Merges a host override over the defaults, section by section. */
export function mergeMoneyMessages(override?: MoneyMessagesOverride): MoneyMessages {
  if (!override) return defaultMoneyMessages;
  const merged = { ...defaultMoneyMessages } as Record<keyof MoneyMessages, unknown>;
  for (const section of Object.keys(override) as (keyof MoneyMessages)[]) {
    merged[section] = { ...defaultMoneyMessages[section], ...override[section] };
  }
  return merged as MoneyMessages;
}

/** The sentence for a refusal: the copy map's sentence for its code, else what the transport said. */
export function describeRefusal(refusal: MoneyRefusal, messages: MoneyMessages = defaultMoneyMessages): string {
  const sentence = messages.refusal[refusal.code]?.trim() || refusal.message.trim() || messages.common.genericRefusal;
  return refusal.retryAfterSeconds ? `${sentence} ${messages.common.retryAfter(refusal.retryAfterSeconds)}` : sentence;
}

/** The person-facing sentence for where a checkout stands. Only `funds_applied` says the balance moved. */
export function describeSettlement(view: Pick<CheckoutView, "status" | "settlementState">, messages: MoneyMessages = defaultMoneyMessages): string {
  if (view.status === "expired" && view.settlementState === "not_started") return messages.checkout.expired;
  return messages.settlement[view.settlementState];
}
