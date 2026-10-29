// The hooks' single intake for host transports. See `src/react/AGENTS.md` §hooks and `src/money/AGENTS.md` §intake.
import { narrowMoneyResult } from "../money/intake.js";
import { moneyRefusal } from "../money/index.js";
import type { MoneyResult } from "../money/index.js";

/**
 * A transport promise that cannot reject or resolve to junk (a broken host transport is an `unconfirmed` refusal,
 * never a crash), narrowed by `narrow` and its refusal re-built from allowlisted fields before any hook state holds it.
 */
export async function settleMoney<T>(pending: Promise<MoneyResult<T>>, narrow: (value: T) => T): Promise<MoneyResult<T>> {
  try {
    return narrowMoneyResult(await pending, narrow);
  } catch {
    return { ok: false, refusal: moneyRefusal("unconfirmed") };
  }
}
