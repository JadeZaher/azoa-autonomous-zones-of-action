// The person's money balance. See `src/react/AGENTS.md` §hooks.
import { useCallback, useEffect, useState } from "react";
import type { BalanceView, MoneyRefusal } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import { settleMoney, useMounted } from "./internal.js";

export interface BalanceState {
  balance: BalanceView | null;
  refusal: MoneyRefusal | null;
  busy: boolean;
  refresh(): Promise<void>;
}

export function useBalance(): BalanceState {
  const { transport } = useMoneyContext();
  const mounted = useMounted();
  const [balance, setBalance] = useState<BalanceView | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    setBusy(true);
    const result = await settleMoney(transport.balance.get());
    if (!mounted.current) return;
    setBusy(false);
    if (result.ok) {
      setBalance(result.value);
      setRefusal(null);
    } else {
      setRefusal(result.refusal);
    }
  }, [transport, mounted]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return { balance, refusal, busy, refresh };
}
