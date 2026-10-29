// Add-funds components: the keyed form, the polled status and the pending-request notice. See `src/react/AGENTS.md` §components.
import { useId, useState } from "react";
import {
  currencyMinorDigits,
  describeRefusal,
  describeSettlement,
  formatMinorUnits,
  isOpenCheckout,
  isSettledCheckout,
  minorUnitsToText,
  moneyRefusal,
  releasesRequestKey,
  safeRedirectUrl,
  toMinorUnits,
} from "../money/index.js";
import type { CheckoutView, MoneyMessages, MoneyRefusal, PendingKind, SavedRequest } from "../money/index.js";
import type { MoneyComponentProps } from "./account-components.js";
import { useMoneyContext } from "./context.js";
import { displayAmount, formatDateTime, redirectTo, useMoneyEvents, useMounted, useSlots } from "./internal.js";
import type { MoneySlots } from "./slots.js";
import { useAddFunds } from "./use-add-funds.js";
import { useCheckoutStatus } from "./use-checkout-status.js";
import type { CheckoutStatusOptions, CheckoutStatusState } from "./use-checkout-status.js";

export interface PendingRequestNoticeProps {
  kind: PendingKind;
  pending: SavedRequest;
  /** For display only; without a known currency the notice names no amount. */
  currencyCode?: string;
  busy?: boolean;
  onResume: () => void;
  onDiscard: () => void;
  slots?: Partial<MoneySlots>;
  className?: string;
}

/** An unresolved saved request blocks a new one until the person resumes or explicitly discards it; one held for review offers neither. */
export function PendingRequestNotice({ kind, pending, currencyCode, busy, onResume, onDiscard, slots, className }: PendingRequestNoticeProps) {
  const { messages } = useMoneyContext();
  const { Alert, Button } = useSlots(slots);
  const summary = messages.pending.summary[kind](formatMinorUnits(pending.amountMinorUnits, currencyCode));
  if (pending.needsReview) {
    // The node holds it for an operator: no resume, no discard into a fresh request; only a same-key re-ask.
    return (
      <Alert tone="warning" className={className}>
        <p>{messages.pending.notConfirmed(summary)}</p>
        <p>{describeRefusal(moneyRefusal("needs_review"), messages)}</p>
        <Button variant="secondary" onClick={onResume} disabled={busy}>{messages.pending.checkAgain}</Button>
      </Alert>
    );
  }
  return (
    <Alert tone="warning" className={className}>
      <p>{messages.pending.notConfirmed(summary)}</p>
      <p>{messages.pending.detail[kind]}</p>
      <Button onClick={onResume} disabled={busy}>{messages.pending.resume[kind]}</Button>
      <Button variant="secondary" onClick={onDiscard} disabled={busy}>{messages.pending.discard}</Button>
    </Alert>
  );
}

export interface CheckoutStatusProps extends MoneyComponentProps, CheckoutStatusOptions {
  intentId: string;
}

/** Where one payment stands, polled until its money settles; only `funds_applied` says the balance moved. */
export function CheckoutStatus(props: CheckoutStatusProps) {
  const status = useCheckoutStatus(props.intentId, props);
  return <CheckoutStatusCard {...props} status={status} />;
}

function CheckoutStatusCard(props: MoneyComponentProps & { status: CheckoutStatusState }) {
  const { messages, events: providerEvents, redirectPolicy } = useMoneyContext();
  const { Card, Button, Alert } = useSlots(props.slots);
  const events = useMoneyEvents(props);
  const mounted = useMounted();
  const { status } = props;
  const view = status.view;
  if (!view) {
    return (
      <Alert tone={status.refusal ? (releasesRequestKey(status.refusal) ? "error" : "warning") : "info"} className={props.className}>
        {status.refusal ? readProblem(status.refusal, messages) : messages.checkout.loading}
      </Alert>
    );
  }
  // The last read failed after this view (LOW-4): it may be stale, so nothing is offered as if it were current.
  const stale = status.refusal !== null && !isSettledCheckout(view);
  // Only a page the person can still pay on is offered; a paid-but-unsettled intent never shows one.
  const open = !stale && isOpenCheckout(view);
  const paymentUrl = open ? safeRedirectUrl(view.paymentUrl, redirectPolicy) : null;
  const canRedirect = Boolean(props.onRedirect ?? providerEvents.onRedirect);
  const paid = view.amountPaidMinorUnits !== null ? formatMinorUnits(view.amountPaidMinorUnits, view.currency) : null;
  const sentence = stale ? messages.checkout.cannotConfirm : describeSettlement(view, messages);
  return (
    <Card title={messages.checkout.title} description={<span role="status">{sentence}</span>} className={props.className}>
      {paid ? <p>{messages.checkout.paid(paid)}</p> : null}
      {open && view.expiresAt ? <p>{messages.checkout.expires(formatDateTime(view.expiresAt))}</p> : null}
      {status.refusal && status.refusal.code !== "unconfirmed" ? <Alert tone="warning">{describeRefusal(status.refusal, messages)}</Alert> : null}
      {paymentUrl && canRedirect ? <Button onClick={() => redirectTo(events, mounted, paymentUrl, "checkout", redirectPolicy)}>{messages.checkout.openPaymentPage}</Button> : null}
      {paymentUrl && !canRedirect ? <a href={paymentUrl}>{messages.checkout.openPaymentPage}</a> : null}
      <Button variant="secondary" onClick={status.refresh} disabled={status.polling}>{messages.checkout.check}</Button>
    </Card>
  );
}

/** A failed read in words: an unanswered one is "can't confirm right now", never "it may not have gone through". */
function readProblem(refusal: MoneyRefusal, messages: MoneyMessages): string {
  return refusal.code === "unconfirmed" ? messages.checkout.cannotConfirm : describeRefusal(refusal, messages);
}

export interface AddFundsFormProps extends MoneyComponentProps {
  /** ISO 4217 code of the node's checkout currency; amounts are parsed with its minor units, and an unknown code accepts none. */
  currencyCode: string;
  /** Quick-pick amounts, in minor units. */
  presets?: number[];
  /** Smallest amount accepted, in minor units. */
  min?: number;
  /** Largest amount accepted, in minor units. */
  max?: number;
  /** The intent id the payment page returned with; resolved by state before anything new is offered. */
  returnedIntentId?: string | null;
  pollIntervalMs?: number;
  /** The payment's money reached a final outcome this device was waiting for; `funds_applied` means the balance moved. */
  onSettled?: (view: CheckoutView) => void;
}

/** Add funds by card: a keyed payment whose key is saved before it leaves and resolved by state afterwards. */
export function AddFundsForm(props: AddFundsFormProps) {
  const { messages } = useMoneyContext();
  const { Card, Button, Input, Alert } = useSlots(props.slots);
  const funds = useAddFunds({ returnedIntentId: props.returnedIntentId, onRedirect: props.onRedirect, onRefusal: props.onRefusal });
  const status = useCheckoutStatus(funds.intentId, {
    initialView: funds.initialView,
    pollIntervalMs: props.pollIntervalMs,
    onSettled: props.onSettled,
  });
  const [amountText, setAmountText] = useState("");
  const [inputError, setInputError] = useState<string | null>(null);
  const inputId = useId();
  const errorId = useId();
  const currency = props.currencyCode;
  const digits = currencyMinorDigits(currency);
  const currencyKnown = digits !== null;
  const unavailable = funds.capabilities !== null && !funds.capabilities.available;
  const trackedView = status.view !== null && status.view.intentId === funds.intentId ? status.view : null;
  // The node definitively will not show this intent (e.g. a stale `?intent=` link): there is nothing it could still charge.
  const definitelyUnreadable = status.refusal !== null && releasesRequestKey(status.refusal);
  // A watched intent without a settled read (open, clearing, paid but not landed, unreadable) could still take money (HIGH-1).
  const settling = funds.intentId !== null && !(trackedView !== null && isSettledCheckout(trackedView)) && !definitelyUnreadable;
  const locked = funds.busy || unavailable || settling;

  const submit = () => {
    if (locked) return;
    if (!currencyKnown) {
      setInputError(messages.common.unknownCurrency);
      return;
    }
    const minorUnits = toMinorUnits(amountText, currency);
    if (minorUnits === null || minorUnits <= 0) {
      setInputError(messages.addFunds.invalidAmount);
      return;
    }
    if ((props.min !== undefined && minorUnits < props.min) || (props.max !== undefined && minorUnits > props.max)) {
      setInputError(messages.addFunds.outOfRange(displayAmount(props.min ?? 1, currency, messages), displayAmount(props.max ?? minorUnits, currency, messages)));
      return;
    }
    setInputError(null);
    void funds.start(minorUnits);
  };

  return (
    <Card title={messages.addFunds.title} description={messages.addFunds.description} className={props.className}>
      {funds.refusal ? <Alert tone="error">{describeRefusal(funds.refusal, messages)}</Alert> : null}
      {funds.notice ? <Alert tone="info">{funds.notice}</Alert> : null}
      {unavailable ? <Alert tone="warning">{funds.capabilities?.unavailableReason ?? messages.addFunds.unavailable}</Alert> : null}
      {funds.pending ? (
        <PendingRequestNotice
          kind="checkout"
          pending={funds.pending}
          currencyCode={currency}
          busy={funds.busy}
          onResume={() => void funds.resume()}
          onDiscard={() => void funds.discard()}
          slots={props.slots}
        />
      ) : (
        <form
          onSubmit={event => {
            event.preventDefault();
            submit();
          }}
        >
          <label htmlFor={inputId}>{messages.addFunds.amountLabel}</label>
          <Input
            id={inputId}
            value={amountText}
            onChange={event => setAmountText(event.target.value)}
            inputMode="decimal"
            placeholder={digits === null ? undefined : minorUnitsToText(25 * 10 ** digits, currency) ?? undefined}
            disabled={settling}
            aria-invalid={inputError ? true : undefined}
            aria-describedby={inputError ? errorId : undefined}
          />
          {currencyKnown
            ? props.presets?.map(preset => (
              <Button key={preset} variant="secondary" disabled={settling} onClick={() => setAmountText(minorUnitsToText(preset, currency) ?? "")}>
                {displayAmount(preset, currency, messages)}
              </Button>
            ))
            : null}
          {inputError ? <p id={errorId}>{inputError}</p> : null}
          <Button type="submit" disabled={locked}>{messages.addFunds.submit}</Button>
        </form>
      )}
      {funds.intentId ? <CheckoutStatusCard status={status} onRedirect={props.onRedirect} slots={props.slots} /> : null}
    </Card>
  );
}
