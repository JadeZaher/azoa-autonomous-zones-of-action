// Maps node `SdkError`s into the closed `MoneyRefusal` vocabulary. See `src/money/AGENTS.md` §refusals.
import { SdkError, SdkErrorCode, isKybRequired, userFacingMessage } from "../core/errors.js";
import { isDefiniteRefusal } from "./presentation.js";
import type { MoneyRefusal, MoneyRefusalCode } from "./types.js";

const REFUSAL_CODES: readonly MoneyRefusalCode[] = [
  "setup_required", "under_review", "consent_revoked", "account_holder_required", "kyb_required",
  "payments_not_configured", "rail_busy", "definite", "unconfirmed", "unauthenticated",
];

/** Codes that leave the request possibly admitted: the saved key must survive them. */
const KEY_KEEPING_CODES: ReadonlySet<MoneyRefusalCode> = new Set(["unconfirmed", "rail_busy", "unauthenticated"]);

const FALLBACK_MESSAGE: Record<MoneyRefusalCode, string> = {
  setup_required: "Finish verifying your identity first.",
  under_review: "An additional compliance check is under review.",
  consent_revoked: "Permission to manage payments for this account was withdrawn.",
  account_holder_required: "Only the account holder can do this.",
  kyb_required: "Business verification is required first.",
  payments_not_configured: "Payments are not configured here.",
  rail_busy: "The payment service is busy. Nothing was moved; try again shortly.",
  definite: "That request was refused. Nothing was moved.",
  unconfirmed: "The answer did not arrive, so it is not known whether this went through.",
  unauthenticated: "Your session expired. Sign in again.",
};

export function isMoneyRefusalCode(value: unknown): value is MoneyRefusalCode {
  return typeof value === "string" && (REFUSAL_CODES as readonly string[]).includes(value);
}

/** True when the refusal is a definite answer, so the saved request key may be forgotten. */
export function releasesRequestKey(refusal: MoneyRefusal): boolean {
  return !KEY_KEEPING_CODES.has(refusal.code);
}

/** Translates an AZOA node failure (or anything a client call threw) into a refusal. Use it server-side in a host BFF too. */
export function refusalFromSdkError(error: unknown): MoneyRefusal {
  if (!(error instanceof SdkError)) return refusal("unconfirmed", "");
  const text = personText(error);
  if (error.code === SdkErrorCode.AUTH_EXPIRED || error.status === 401) return refusal("unauthenticated", "");
  if (isKybRequired(error)) return refusal("kyb_required", text);
  if (error.serverCode === "PAYMENTS_NOT_CONFIGURED") return refusal("payments_not_configured", text);
  if (error.serverCode === "RAIL_BUSY" || error.serverCode === "RAIL_UNAVAILABLE" || error.status === 429) return refusal("rail_busy", text);
  // Thrown by the client's own guards before any request left (e.g. a malformed intent id from a return URL).
  if (error.code === SdkErrorCode.INVALID_INPUT) return refusal("definite", "");
  if (isDefiniteRefusal(error.status)) {
    const raw = error.serverMessage ?? error.message;
    if (error.status === 403 && /account holder/i.test(raw)) return refusal("account_holder_required", text);
    if (error.status === 403 && /consent/i.test(raw)) return refusal("consent_revoked", text);
    if (/under review/i.test(raw)) return refusal("under_review", text);
    if (/KYC verification required/i.test(raw)) return refusal("setup_required", text);
    return refusal("definite", text);
  }
  // Network loss, timeouts, 409 (in progress) and 5xx: the request may have been admitted.
  return refusal("unconfirmed", text);
}

function refusal(code: MoneyRefusalCode, message: string): MoneyRefusal {
  return { code, message: message || FALLBACK_MESSAGE[code] };
}

/** The server's own sentence without wire-code prefixes (`KYB_REQUIRED: `) or configuration key names (QA L2); never SDK-generated text. */
function personText(error: SdkError): string {
  if (!error.serverMessage) return "";
  return userFacingMessage(error)
    .replace(/^(?:[A-Z][A-Z_]+:\s*)+/, "")
    .replace(/\s*Missing configuration:[\s\S]*$/, "")
    .trim();
}
