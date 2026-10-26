// Host-priced sale form and the balance summary. See `src/react/AGENTS.md` §components.
import { useId, useState } from "react";
import { describeRefusal, formatBaseUnits, formatMinorUnits, toBaseUnits } from "../money/index.js";
import type { SaleView } from "../money/index.js";
import type { MoneyComponentProps } from "./account-components.js";
import { useMoneyContext } from "./context.js";
import { formatDateTime, useSlots } from "./internal.js";
import { PendingRequestNotice } from "./funds-components.js";
import { useBalance } from "./use-balance.js";
import { useSellUnits } from "./use-sell-units.js";

export interface SellUnitsFormProps extends MoneyComponentProps {
  /** A sale was accepted. */
  onSettled?: (view: SaleView) => void;
}

/** Sell units back to the host at its quoted price; renders nothing when the host's transport has no `sale`. */
export function SellUnitsForm(props: SellUnitsFormProps) {
  const { messages } = useMoneyContext();
  const { Card, Button, Input, Alert } = useSlots(props.slots);
  const sale = useSellUnits({ onRefusal: props.onRefusal, onSettled: props.onSettled });
  const [quantityText, setQuantityText] = useState("");
  const [inputError, setInputError] = useState<string | null>(null);
  const inputId = useId();
  if (!sale.supported) return null;
  const sellable = sale.sellable;

  const requestQuote = () => {
    if (!sellable) return;
    const quantity = toBaseUnits(quantityText, sellable.decimals);
    const available = /^\d+$/.test(sellable.availableBaseUnits) ? BigInt(sellable.availableBaseUnits) : 0n;
    if (quantity === null || BigInt(quantity) <= 0n || BigInt(quantity) > available) {
      setInputError(messages.sale.invalidQuantity(`${formatBaseUnits(sellable.availableBaseUnits, sellable.decimals)} ${sellable.unitName}`));
      return;
    }
    setInputError(null);
    void sale.requestQuote(quantity);
  };

  return (
    <Card title={messages.sale.title} description={sellable?.statusMessage} className={props.className}>
      {sale.refusal ? <Alert tone="error">{describeRefusal(sale.refusal, messages)}</Alert> : null}
      {sale.notice ? <Alert tone="success">{sale.notice}</Alert> : null}
      {sellable ? <p>{messages.sale.availableLabel}: {formatBaseUnits(sellable.availableBaseUnits, sellable.decimals)} {sellable.unitName}</p> : null}
      {sale.pending ? (
        <PendingRequestNotice
          kind="sale"
          pending={sale.pending}
          currencyCode={sellable?.currencyCode}
          busy={sale.busy}
          onResume={() => void sale.resume()}
          onDiscard={sale.discard}
          slots={props.slots}
        />
      ) : (
        <form
          onSubmit={event => {
            event.preventDefault();
            requestQuote();
          }}
        >
          <label htmlFor={inputId}>{messages.sale.quantityLabel}</label>
          <Input id={inputId} value={quantityText} onChange={event => setQuantityText(event.target.value)} inputMode="decimal" aria-invalid={inputError ? true : undefined} />
          {inputError ? <p>{inputError}</p> : null}
          <Button type="submit" variant="secondary" disabled={sale.busy || !sellable}>{messages.sale.getQuote}</Button>
        </form>
      )}
      {sale.quote && sellable && !sale.pending ? (
        <>
          <p>
            {messages.sale.quoteSummary(
              `${formatBaseUnits(sale.quote.quantityBaseUnits, sellable.decimals)} ${sellable.unitName}`,
              formatMinorUnits(sale.quote.proceedsMinorUnits, sale.quote.currencyCode),
            )}
          </p>
          {sale.quote.expiresAt ? <p>{messages.sale.quoteExpires(formatDateTime(sale.quote.expiresAt))}</p> : null}
          <Button onClick={() => void sale.sell()} disabled={sale.busy}>{messages.sale.submit}</Button>
        </>
      ) : null}
    </Card>
  );
}

/** The person's money balance: what may leave today and what is still on hold. */
export function BalanceSummary(props: MoneyComponentProps) {
  const { messages } = useMoneyContext();
  const { Card, Button, Alert } = useSlots(props.slots);
  const { balance, refusal, busy, refresh } = useBalance();
  return (
    <Card title={messages.balance.title} className={props.className}>
      {refusal ? <Alert tone="error">{describeRefusal(refusal, messages)}</Alert> : null}
      {balance ? (
        <dl>
          <dt>{messages.balance.availableLabel}</dt>
          <dd>{formatMinorUnits(balance.availableMinorUnits, balance.currencyCode)}</dd>
          <dt>{messages.balance.onHoldLabel}</dt>
          <dd>{formatMinorUnits(balance.onHoldMinorUnits, balance.currencyCode)}</dd>
        </dl>
      ) : null}
      {balance?.availableFrom && balance.onHoldMinorUnits > 0 ? <p>{messages.balance.availableFrom(formatDateTime(balance.availableFrom))}</p> : null}
      <Button variant="secondary" onClick={() => void refresh()} disabled={busy}>{messages.common.refresh}</Button>
    </Card>
  );
}
