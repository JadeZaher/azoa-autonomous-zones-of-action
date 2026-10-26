// Rejection guard for host transports. See `src/react/AGENTS.md` §hooks.
import type { MoneyResult } from "../money/index.js";

/** A transport promise that cannot reject: a broken host transport surfaces as an `unconfirmed` refusal, never a crash. */
export async function settleMoney<T>(pending: Promise<MoneyResult<T>>): Promise<MoneyResult<T>> {
  try {
    return await pending;
  } catch {
    return { ok: false, refusal: { code: "unconfirmed", message: "" } };
  }
}
