// Amount parsing/formatting, checkout state predicates, the redirect policy and the default copy map. See `src/money/AGENTS.md` §presentation.
import type { FiatSettlementState, TenantKycStatus } from "../api/client.js";
import type { AccountGateState, CheckoutView, MoneyRefusal, MoneyRefusalCode } from "./types.js";

/** The fields every checkout predicate reads; typed loosely because host transports deserialize JSON at runtime. */
export interface CheckoutState {
  status: string;
  settlementState: string;
}

const TERMINAL_CHECKOUT_STATUSES: readonly string[] = ["complete", "failed", "expired"];

/** The node will never reopen this intent (a same-key resend is refused), so its saved key may be forgotten. Not "settled". */
export function isTerminalCheckout(intent: CheckoutState): boolean {
  return TERMINAL_CHECKOUT_STATUSES.includes(intent.status) || isSettledCheckout(intent);
}

/** The money outcome is final, so polling may stop: funds applied, payment failed, or a failed/expired intent where nothing cleared. Unknown values are never final. */
export function isSettledCheckout(intent: CheckoutState): boolean {
  if (intent.settlementState === "funds_applied" || intent.settlementState === "payment_failed") return true;
  return intent.settlementState === "not_started" && (intent.status === "failed" || intent.status === "expired");
}

/** The person still holds a page they can pay on. */
export function isOpenCheckout(intent: CheckoutState): boolean {
  return intent.status === "awaiting_payment" && intent.settlementState === "not_started";
}

/** A cash-out status that is on its way or paid (the node's `FiatPayoutStatuses`); anything else needs the person's attention. */
export function isCashOutOnTrack(view: { status: string }): boolean {
  return ["requested", "unitscollected", "bankpending", "paid"].includes(String(view.status).toLowerCase());
}

/** An HTTP refusal that is a definite answer for a request key (400/403/404/422); anything else may have been admitted. */
export function isDefiniteRefusal(status: number | undefined): boolean {
  return status === 400 || status === 403 || status === 404 || status === 422;
}

// ISO 4217 minor units (List One, 2025): every active code whose minor unit is not 2, then every code whose minor unit is 2.
const MINOR_DIGITS_NOT_TWO = new Map<string, number>([
  ...["BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "UYI", "VND", "VUV", "XAF", "XOF", "XPF"].map(code => [code, 0] as const),
  ...["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"].map(code => [code, 3] as const),
  ...["CLF", "UYW"].map(code => [code, 4] as const),
]);
const MINOR_DIGITS_TWO = new Set(
  ("AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BMD BND BOB BOV BRL BSD BTN BWP BYN BZD CAD CDF CHE CHF CHW CNY COP "
    + "COU CRC CUC CUP CVE CZK DKK DOP DZD EGP ERN ETB EUR FJD FKP GBP GEL GHS GIP GMD GTQ GYD HKD HNL HRK HTG HUF IDR ILS INR IRR "
    + "JMD KES KGS KHR KPW KYD KZT LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MXV MYR MZN NAD NGN NIO NOK "
    + "NPR NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP SLE SLL SOS SRD SSP STN SVC SYP SZL THB TJS TMT "
    + "TOP TRY TTD TWD TZS UAH USD USN UYU UZS VED VES WST XCD XCG YER ZAR ZMW ZWG ZWL").split(" "),
);

/** Digits between a minor and a major unit of `currencyCode` (ISO 4217), or null for a code this SDK does not know. */
export function currencyMinorDigits(currencyCode: string | null | undefined): number | null {
  if (typeof currencyCode !== "string") return null;
  const code = currencyCode.trim().toUpperCase();
  return MINOR_DIGITS_NOT_TWO.get(code) ?? (MINOR_DIGITS_TWO.has(code) ? 2 : null);
}

/** Parses a decimal amount in major units (e.g. "12.50") into integer minor units of `currencyCode`; null when not exact or the currency is unknown. */
export function toMinorUnits(text: string, currencyCode: string | null | undefined): number | null {
  const digits = currencyMinorDigits(currencyCode);
  if (digits === null) return null;
  const match = /^(\d{1,9})(?:\.(\d+))?$/.exec(text.trim());
  const fraction = match?.[2] ?? "";
  if (!match || fraction.length > digits) return null;
  return Number(match[1]) * 10 ** digits + Number(fraction.padEnd(digits, "0") || "0");
}

/** Formats integer minor units for display (`1,234.50 USD`); null when the currency is unknown, never a guessed decimal point. */
export function formatMinorUnits(minorUnits: number, currencyCode: string | null | undefined): string | null {
  const text = minorUnitsToText(Math.abs(minorUnits), currencyCode, true);
  return text === null ? null : `${minorUnits < 0 ? "-" : ""}${text} ${currencyCode!.trim().toUpperCase()}`;
}

/** Minor units as plain decimal text `toMinorUnits` parses back (`1234.50`); null when the currency is unknown. */
export function minorUnitsToText(minorUnits: number, currencyCode: string | null | undefined, grouped = false): string | null {
  const digits = currencyMinorDigits(currencyCode);
  if (digits === null || !Number.isSafeInteger(minorUnits) || minorUnits < 0) return null;
  const scale = 10 ** digits;
  const whole = Math.floor(minorUnits / scale);
  const fraction = digits > 0 ? `.${String(minorUnits % scale).padStart(digits, "0")}` : "";
  return `${grouped ? whole.toLocaleString("en-US") : String(whole)}${fraction}`;
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

/** Where a hosted page (payment, verification, payout setup) may send the person. */
export interface RedirectPolicy {
  /** Exact origins (e.g. `https://checkout.stripe.com`) a redirect or link may go to; omitted means any `https:` origin, empty means none. */
  allowedOrigins?: readonly string[];
  /** Development only: also admit `http:` on loopback (`localhost`, `127.0.0.1`, `[::1]`). */
  allowLoopbackHttp?: boolean;
}

const LOOPBACK_HOSTS: readonly string[] = ["localhost", "127.0.0.1", "[::1]"];

/** The parsed, normalized URL when `policy` admits it, else null; a transport answer can never hand `javascript:` to a link. */
export function safeRedirectUrl(url: string | null | undefined, policy: RedirectPolicy = {}): string | null {
  if (typeof url !== "string" || url === "") return null;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  const secure = parsed.protocol === "https:";
  const devLoopback = policy.allowLoopbackHttp === true && parsed.protocol === "http:" && LOOPBACK_HOSTS.includes(parsed.hostname);
  if (!secure && !devLoopback) return null;
  // `https://trusted@elsewhere` reads as the trusted host to a person and goes to `elsewhere`.
  if (parsed.username || parsed.password) return null;
  if (policy.allowedOrigins && !policy.allowedOrigins.some(origin => originOf(origin) === parsed.origin)) return null;
  return parsed.href;
}

function originOf(candidate: string): string | null {
  try {
    return new URL(candidate).origin;
  } catch {
    return null;
  }
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
  checkout: {
    title: string; loading: string; expired: string; confirming: string; openPaymentPage: string; check: string;
    paid: (amount: string) => string; expires: (date: string) => string;
  };
  pending: {
    notConfirmed: (summary: string) => string;
    /** `amount` is null when the currency is unknown, so no amount is guessed. */
    summary: Record<PendingKind, (amount: string | null) => string>;
    detail: Record<PendingKind, string>;
    resume: Record<PendingKind, string>;
    discard: string;
    discarded: string;
    /** The saved request changed in another window before this one resent it; nothing was sent. */
    superseded: string;
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
  common: { refresh: string; retryAfter: (seconds: number) => string; genericRefusal: string; unknownCurrency: string; amountUnavailable: string };
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
    confirming: "Your payment page is finished. Confirming the payment with the provider…",
    openPaymentPage: "Continue to the payment page",
    check: "Check payment",
    paid: amount => `Paid: ${amount}`,
    expires: date => `The payment page works until ${date}.`,
  },
  pending: {
    notConfirmed: summary => `${summary} has not been confirmed yet.`,
    summary: {
      checkout: amount => (amount ? `A payment of ${amount}` : "A payment"),
      cashOut: amount => (amount ? `A cash-out of ${amount}` : "A cash-out"),
      sale: amount => (amount ? `A sale for ${amount}` : "A sale"),
    },
    detail: {
      checkout: "Resuming reopens that same payment instead of starting a second one. Discard it only if you want a separate payment; a payment page you already completed still counts.",
      cashOut: "Resuming sends the same request again, and it is paid at most once. If it is already in the history, it was received. Discard it only if you want a separate cash-out.",
      sale: "Resuming sends the same sale again; it completes at most once. Discard it only if you want a separate sale.",
    },
    resume: { checkout: "Resume that payment", cashOut: "Resume that cash-out", sale: "Resume that sale" },
    discard: "Discard it and start a new request",
    discarded: "The unconfirmed request was discarded. Enter an amount to start a new one.",
    superseded: "That request was already settled in another window, so nothing was sent again.",
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
    unknownCurrency: "Amounts in this currency cannot be entered here, so nothing was sent.",
    amountUnavailable: "—",
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

/** The person-facing sentence for where a checkout stands. Only `funds_applied` says the balance moved; only an open page says "waiting". */
export function describeSettlement(view: Pick<CheckoutView, "status" | "settlementState">, messages: MoneyMessages = defaultMoneyMessages): string {
  const status: string = view.status;
  const settlement: string = view.settlementState;
  if (status === "expired" && (settlement === "not_started" || settlement === "payment_failed")) return messages.checkout.expired;
  if (settlement === "funds_applied" || settlement === "payment_received" || settlement === "payment_failed") {
    return messages.settlement[settlement as FiatSettlementState];
  }
  if (settlement === "not_started") {
    if (status === "failed") return messages.settlement.payment_failed;
    if (status === "awaiting_payment" || status === "starting") return messages.settlement.not_started;
  }
  // `complete`/`processing` with nothing cleared yet (or a value this SDK does not know): never "waiting for your payment".
  return messages.checkout.confirming;
}
