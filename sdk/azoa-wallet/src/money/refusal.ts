// Maps node `SdkError`s into the closed `MoneyRefusal` vocabulary. See `src/money/AGENTS.md` §refusals.
import { KYB_REQUIRED, SdkError, SdkErrorCode, isKybRequired, userFacingMessage } from "../core/errors.js";
import { isDefiniteRefusal } from "./presentation.js";
import type { MoneyRefusal, MoneyRefusalCode } from "./types.js";

const REFUSAL_CODES: readonly MoneyRefusalCode[] = [
  "setup_required", "under_review", "consent_revoked", "account_holder_required", "kyb_required",
  "payments_not_configured", "rail_busy", "definite", "unconfirmed", "needs_review", "unauthenticated",
];

/** The only codes that are a definite answer for a request key. An allowlist: an unknown, misspelled or future code keeps the key. */
const KEY_RELEASING_CODES: ReadonlySet<string> = new Set<MoneyRefusalCode>([
  "definite", "setup_required", "under_review", "consent_revoked", "account_holder_required", "kyb_required", "payments_not_configured",
]);

/** Node envelope codes (`AZOAResult.code`) with a fixed meaning; a key-releasing code counts only on the statuses the node sends it with. */
const SERVER_CODE_RULES = new Map<string, { refusal: MoneyRefusalCode; statuses?: readonly number[] }>([
  [KYB_REQUIRED, { refusal: "kyb_required", statuses: [400, 403, 422] }],
  ["PAYMENTS_NOT_CONFIGURED", { refusal: "payments_not_configured", statuses: [400, 422, 503] }],
  ["RAIL_BUSY", { refusal: "rail_busy" }],
  ["RAIL_UNAVAILABLE", { refusal: "rail_busy" }],
  // Same-key 409s from `FiatRailProvider.BeginCheckoutAsync`. Final only for a key that names no intent yet;
  // a saved request with a `serverId` is decided by that intent's state instead (see `flows.ts`).
  ["INTENT_FINISHED", { refusal: "definite", statuses: [409] }],
  ["INTENT_EXPIRED", { refusal: "definite", statuses: [409] }],
  ["REQUEST_KEY_IN_USE", { refusal: "definite", statuses: [409] }],
  // A begin on a voided key: the node voided it, so nothing was or can be paid under it.
  ["REQUEST_VOIDED", { refusal: "definite", statuses: [409] }],
  // The node holds the request for an operator's decision and it may still complete: the key must survive.
  ["INTENT_NEEDS_REVIEW", { refusal: "needs_review", statuses: [409] }],
]);

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
  needs_review: "This payment needs a manual check before it can continue — we'll follow up.",
  unauthenticated: "Your session expired. Sign in again.",
};

export function isMoneyRefusalCode(value: unknown): value is MoneyRefusalCode {
  return typeof value === "string" && (REFUSAL_CODES as readonly string[]).includes(value);
}

/** True only when the refusal is a definite answer, so the saved request key may be forgotten; junk or unknown codes keep it. */
export function releasesRequestKey(refusal: MoneyRefusal | null | undefined): boolean {
  const code: unknown = refusal?.code;
  return typeof code === "string" && KEY_RELEASING_CODES.has(code);
}

/** A refusal with `message` (or the code's fixed fallback sentence when it is blank). */
export function moneyRefusal(code: MoneyRefusalCode, message = ""): MoneyRefusal {
  return { code, message: message.trim() || FALLBACK_MESSAGE[code] };
}

/** Translates an AZOA node failure (or anything a client call threw) into a refusal. Use it server-side in a host BFF too. */
export function refusalFromSdkError(error: unknown): MoneyRefusal {
  if (!(error instanceof SdkError)) return moneyRefusal("unconfirmed");
  const status = error.status;
  if (error.code === SdkErrorCode.AUTH_EXPIRED || status === 401) return moneyRefusal("unauthenticated");
  // No HTTP answer: a client guard that threw before egress is definite; a lost or timed-out request may have been admitted.
  if (status === undefined) return moneyRefusal(error.code === SdkErrorCode.INVALID_INPUT ? "definite" : "unconfirmed");
  const text = personText(error);
  // The envelope's machine code outranks status and message text.
  const rule = error.serverCode === undefined ? undefined : SERVER_CODE_RULES.get(error.serverCode);
  if (rule && (!rule.statuses || rule.statuses.includes(status))) {
    const refusal = moneyRefusal(rule.refusal, text);
    // Only these same-key codes ever carry a result (the node's own projection); surfacing it lets a
    // caller decide by that state instead of a second round trip (see flows.ts `beginCheckout`).
    return error.serverResult === undefined ? refusal : { ...refusal, serverResult: error.serverResult };
  }
  if (status === 429) return moneyRefusal("rail_busy", text);
  // 409 without a known code, 408, 5xx: the request may have been admitted, whatever the message says.
  if (!isDefiniteRefusal(status)) return moneyRefusal("unconfirmed", text);
  const raw = error.serverMessage ?? error.message;
  if (isKybRequired(error)) return moneyRefusal("kyb_required", text);
  if (status === 403 && /account holder/i.test(raw)) return moneyRefusal("account_holder_required", text);
  if (status === 403 && /consent/i.test(raw)) return moneyRefusal("consent_revoked", text);
  if (/under review/i.test(raw)) return moneyRefusal("under_review", text);
  if (/KYC verification required/i.test(raw)) return moneyRefusal("setup_required", text);
  return moneyRefusal("definite", text);
}

/** The server's own sentence without wire-code prefixes (`KYB_REQUIRED: `) or configuration key names (QA L2); never SDK-generated text. */
function personText(error: SdkError): string {
  if (!error.serverMessage) return "";
  return userFacingMessage(error)
    .replace(/^(?:[A-Z][A-Z_]+:\s*)+/, "")
    .replace(/\s*Missing configuration:[\s\S]*$/, "")
    .trim();
}
