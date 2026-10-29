// The person's money balance. See `src/react/AGENTS.md` §hooks.
import { useCallback, useEffect, useState } from "react";
import { toBalanceView } from "../money/index.js";
import type { BalanceView, MoneyRefusal } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import { settleMoney, useMounted, usePerson } from "./internal.js";

export interface BalanceState {
  balance: BalanceView | null;
  refusal: MoneyRefusal | null;
  busy: boolean;
  refresh(): Promise<void>;
}

export function useBalance(): BalanceState {
  const { transport, identity } = useMoneyContext();
  const mounted = useMounted();
  const [balance, setBalance] = useState<BalanceView | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [busy, setBusy] = useState(false);
  // A new person never sees the previous one's balance, even for one paint; a read for the previous one is dropped.
  const person = usePerson(() => {
    setBalance(null);
    setRefusal(null);
  });

  const refresh = useCallback(async () => {
    const readFor = person.current;
    setBusy(true);
    const result = await settleMoney(transport.balance.get(), toBalanceView);
    if (!mounted.current || person.current !== readFor) return;
    setBusy(false);
    if (result.ok) {
      setBalance(result.value);
      setRefusal(null);
    } else {
      setRefusal(result.refusal);
    }
  }, [transport, mounted, person]);

  useEffect(() => {
    void refresh();
  }, [refresh, identity]);

  return { balance, refusal, busy, refresh };
}
