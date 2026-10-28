// Cash-out components: payout destination setup and the keyed withdraw form. See `src/react/AGENTS.md` §components.
import { useId, useState } from "react";
import { currencyMinorDigits, describeRefusal, safeRedirectUrl, toMinorUnits } from "../money/index.js";
import type { CashOutView } from "../money/index.js";
import type { MoneyComponentProps } from "./account-components.js";
import { useMoneyContext } from "./context.js";
import { callHost, displayAmount, formatDateTime, useSlots } from "./internal.js";
import { PendingRequestNotice } from "./funds-components.js";
import { useCashOut } from "./use-cash-out.js";
import type { CashOutState } from "./use-cash-out.js";

export interface PayoutDestinationStepProps extends MoneyComponentProps {
  /** Where the account holder sets up payouts themselves (e.g. their AZOA account page), offered when this app may not. */
  accountHolderSetupUrl?: string;
}

/** Set up (or continue setting up) where cash-outs are paid; explains when only the account holder can. */
export function PayoutDestinationStep(props: PayoutDestinationStepProps) {
  const cashOut = useCashOut({ onRedirect: props.onRedirect, onRefusal: props.onRefusal });
  return <PayoutDestinationPanel {...props} cashOut={cashOut} />;
}

function PayoutDestinationPanel(props: PayoutDestinationStepProps & { cashOut: CashOutState }) {
  const { messages, events: providerEvents, redirectPolicy } = useMoneyContext();
  const { Card, Button, Alert } = useSlots(props.slots);
  const { destination, destinationRefusal, busy, canRedirect } = props.cashOut;
  // F-H8: the node refuses tenant-initiated payout setup; that is a route to the account holder, not an error.
  const holderOnly = destinationRefusal?.code === "account_holder_required";
  const setupUrl = safeRedirectUrl(destination?.setupUrl, redirectPolicy);
  const holderUrl = safeRedirectUrl(props.accountHolderSetupUrl, redirectPolicy);
  const onRedirect = props.onRedirect ?? providerEvents.onRedirect;
  return (
    <Card title={messages.destination.title} description={destination?.statusMessage} className={props.className}>
      {holderOnly ? <Alert tone="info">{messages.destination.accountHolderOnly}</Alert> : null}
      {destinationRefusal && !holderOnly ? <Alert tone="error">{describeRefusal(destinationRefusal, messages)}</Alert> : null}
      {destination && !destination.readyToReceive && !holderOnly ? (
        <Button onClick={() => void props.cashOut.beginDestinationSetup()} disabled={busy || !destination.available}>
          {destination.accountOpened ? messages.destination.continue : messages.destination.start}
        </Button>
      ) : null}
      {setupUrl && !canRedirect ? <a href={setupUrl}>{messages.destination.openSetupPage}</a> : null}
      {holderOnly && holderUrl && onRedirect ? <Button onClick={() => callHost(() => onRedirect(holderUrl, "payout-setup"))}>{messages.destination.accountHolderAction}</Button> : null}
      {holderOnly && holderUrl && !onRedirect ? <a href={holderUrl}>{messages.destination.accountHolderAction}</a> : null}
      {destination && destination.outstandingRequirementCount > 0 ? <p>{messages.destination.outstanding(destination.outstandingRequirementCount)}</p> : null}
    </Card>
  );
}

export interface WithdrawFormProps extends PayoutDestinationStepProps {
  /** A cash-out was accepted (never a refused one); its `status` says how far it got. */
  onSettled?: (view: CashOutView) => void;
}

/** Cash out to the person's own payout account with a keyed request; history is re-read after every attempt. */
export function WithdrawForm(props: WithdrawFormProps) {
  const { messages } = useMoneyContext();
  const { Card, Button, Input, Alert } = useSlots(props.slots);
  const cashOut = useCashOut({ onRedirect: props.onRedirect, onRefusal: props.onRefusal, onSettled: props.onSettled });
  const [amountText, setAmountText] = useState("");
  const [inputError, setInputError] = useState<string | null>(null);
  const inputId = useId();
  const quote = cashOut.quote;
  const ready = Boolean(quote?.available && quote.accountReady);

  const submit = () => {
    if (!quote || cashOut.busy) return;
    if (currencyMinorDigits(quote.currencyCode) === null) {
      setInputError(messages.common.unknownCurrency);
      return;
    }
    const ceiling = Math.min(quote.maximumAmountMinorUnits, quote.cashableMinorUnits);
    const minorUnits = toMinorUnits(amountText, quote.currencyCode);
    if (minorUnits === null || minorUnits < quote.minimumAmountMinorUnits || minorUnits > ceiling) {
      setInputError(messages.cashOut.outOfRange(
        displayAmount(quote.minimumAmountMinorUnits, quote.currencyCode, messages),
        displayAmount(ceiling, quote.currencyCode, messages),
      ));
      return;
    }
    setInputError(null);
    void cashOut.start(minorUnits);
  };

  return (
    <Card title={messages.cashOut.title} description={quote?.statusMessage} className={props.className}>
      {cashOut.refusal ? <Alert tone="error">{describeRefusal(cashOut.refusal, messages)}</Alert> : null}
      {cashOut.notice ? <Alert tone={cashOut.noticeTone}>{cashOut.notice}</Alert> : null}
      {cashOut.destination && !cashOut.destination.readyToReceive ? (
        <PayoutDestinationPanel slots={props.slots} onRedirect={props.onRedirect} onRefusal={props.onRefusal} accountHolderSetupUrl={props.accountHolderSetupUrl} cashOut={cashOut} />
      ) : null}
      {quote ? (
        <dl>
          <dt>{messages.cashOut.cashableLabel}</dt>
          <dd>{displayAmount(quote.cashableMinorUnits, quote.currencyCode, messages)}</dd>
          <dt>{messages.cashOut.onHoldLabel}</dt>
          <dd>{displayAmount(quote.onHoldMinorUnits, quote.currencyCode, messages)}</dd>
          {quote.availableFrom ? (
            <>
              <dt>{messages.cashOut.availableFromLabel}</dt>
              <dd>{formatDateTime(quote.availableFrom)}</dd>
            </>
          ) : null}
        </dl>
      ) : null}
      {cashOut.pending ? (
        <PendingRequestNotice
          kind="cashOut"
          pending={cashOut.pending}
          currencyCode={quote?.currencyCode}
          busy={cashOut.busy}
          onResume={() => void cashOut.resume()}
          onDiscard={cashOut.discard}
          slots={props.slots}
        />
      ) : (
        <form
          onSubmit={event => {
            event.preventDefault();
            submit();
          }}
        >
          <label htmlFor={inputId}>{messages.cashOut.amountLabel}</label>
          <Input id={inputId} value={amountText} onChange={event => setAmountText(event.target.value)} inputMode="decimal" placeholder="50.00" aria-invalid={inputError ? true : undefined} />
          {inputError ? <p>{inputError}</p> : null}
          {quote && !quote.accountReady ? <p>{messages.cashOut.notReady}</p> : null}
          <Button type="submit" disabled={cashOut.busy || !ready}>{messages.cashOut.submit}</Button>
        </form>
      )}
      <Button variant="secondary" onClick={() => void cashOut.reload()} disabled={cashOut.busy}>{messages.common.refresh}</Button>
      <h4>{messages.cashOut.historyTitle}</h4>
      {cashOut.history.length === 0 ? (
        <p>{messages.cashOut.historyEmpty}</p>
      ) : (
        <ul>
          {cashOut.history.map(row => (
            <li key={row.id}>
              <span>{displayAmount(row.amountMinorUnits, row.currencyCode, messages)}</span> <span>{row.statusMessage}</span>{" "}
              <span>{formatDateTime(row.requestedAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
