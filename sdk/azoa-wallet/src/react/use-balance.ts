// The person's money balance. See `src/react/AGENTS.md` §hooks.
import { useCallback, useEffect, useRef, useState } from "react";
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
  const { transport, keyStore } = useMoneyContext();
  const mounted = useMounted();
  const [balance, setBalance] = useState<BalanceView | null>(null);
  const [refusal, setRefusal] = useState<MoneyRefusal | null>(null);
  const [busy, setBusy] = useState(false);
  // Bumped per subject; a read that belongs to an older one is dropped.
  const epoch = useRef(0);

  const refresh = useCallback(async () => {
    const readIn = epoch.current;
    setBusy(true);
    const result = await settleMoney(transport.balance.get());
    if (!mounted.current || epoch.current !== readIn) return;
    setBusy(false);
    if (result.ok) {
      setBalance(result.value);
      setRefusal(null);
    } else {
      setRefusal(result.refusal);
    }
  }, [transport, mounted]);

  useEffect(() => {
    epoch.current += 1;
    setBalance(null);
    setRefusal(null);
    void refresh();
  }, [refresh, keyStore]);

  return { balance, refusal, busy, refresh };
}
