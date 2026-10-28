// Rejection guard for host transports. See `src/react/AGENTS.md` §hooks.
import { moneyRefusal } from "../money/index.js";
import type { MoneyResult } from "../money/index.js";

/** A transport promise that cannot reject or resolve to junk: a broken host transport surfaces as an `unconfirmed` refusal, never a crash. */
export async function settleMoney<T>(pending: Promise<MoneyResult<T>>): Promise<MoneyResult<T>> {
  try {
    const result = await pending;
    if (result !== null && typeof result === "object") {
      if (result.ok === true && result.value !== null && result.value !== undefined) return result;
      if (result.ok === false && typeof result.refusal === "object" && result.refusal !== null) return result;
    }
  } catch {
    // Falls through to unconfirmed.
  }
  return { ok: false, refusal: moneyRefusal("unconfirmed") };
}
