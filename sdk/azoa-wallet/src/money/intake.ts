// The one intake every transport answer passes before a flow or hook keeps it. See `src/money/AGENTS.md` §intake.
import { isMoneyRefusalCode, moneyRefusal } from "./refusal.js";
import type { CashOutView, CheckoutView, MoneyRefusal, MoneyRefusalCode, MoneyResult, SaleView } from "./types.js";
import { toCashOutView, toCheckoutView, toRefusalServerResult, toSaleView } from "./views.js";

/** The refusal codes whose node evidence can carry the key's own intent (`INTENT_FINISHED` / `INTENT_EXPIRED` → `definite`, `INTENT_NEEDS_REVIEW`). */
const RESULT_CARRYING_REFUSALS: ReadonlySet<MoneyRefusalCode> = new Set<MoneyRefusalCode>(["definite", "needs_review"]);

/** A well-formed `MoneyResult` whose value `narrow` accepts (allowlist-copied), else `unconfirmed`: the request may have been admitted. */
export function narrowMoneyResult<T>(result: unknown, narrow: (value: T) => T): MoneyResult<T> {
  if (isRecord(result) && result.ok === false && isRecord(result.refusal)) return { ok: false, refusal: narrowRefusal(result.refusal) };
  if (isRecord(result) && result.ok === true && result.value !== null && result.value !== undefined) {
    try {
      return { ok: true, value: narrow(result.value as T) };
    } catch {
      // Not the view: falls through to unconfirmed.
    }
  }
  return { ok: false, refusal: moneyRefusal("unconfirmed") };
}

/** A refusal rebuilt from its allowlisted fields: an unknown code is `unconfirmed` (keeps the key); `serverResult` is re-narrowed or dropped. */
export function narrowRefusal(raw: Record<string, unknown>): MoneyRefusal {
  const code = isMoneyRefusalCode(raw.code) ? raw.code : "unconfirmed";
  const refusal = moneyRefusal(code, typeof raw.message === "string" ? raw.message : "");
  const retry = raw.retryAfterSeconds;
  if (typeof retry === "number" && Number.isFinite(retry) && retry >= 0) refusal.retryAfterSeconds = retry;
  const serverResult = RESULT_CARRYING_REFUSALS.has(code) ? toRefusalServerResult(raw.serverResult) : null;
  if (serverResult) refusal.serverResult = serverResult;
  return refusal;
}

/** `narrow` for a checkout: any record with a string `intentId`, allowlist-copied; throws for anything else. */
export function checkoutViewFrom(value: unknown): CheckoutView {
  if (!isRecord(value) || typeof value.intentId !== "string") throw new TypeError("Not a checkout view.");
  return toCheckoutView(value as unknown as CheckoutView);
}

/** `narrow` for one cash-out: any record with a string `id`, allowlist-copied; throws for anything else. */
export function cashOutViewFrom(value: unknown): CashOutView {
  if (!isRecord(value) || typeof value.id !== "string") throw new TypeError("Not a cash-out view.");
  return toCashOutView(value as unknown as CashOutView);
}

/** `narrow` for cash-out history: an array of cash-outs; throws for anything else. */
export function cashOutHistoryFrom(value: unknown): CashOutView[] {
  if (!Array.isArray(value)) throw new TypeError("Not a cash-out history.");
  return value.map(cashOutViewFrom);
}

/** `narrow` for one sale: any record with a string `id`, allowlist-copied; throws for anything else. */
export function saleViewFrom(value: unknown): SaleView {
  if (!isRecord(value) || typeof value.id !== "string") throw new TypeError("Not a sale view.");
  return toSaleView(value as unknown as SaleView);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
