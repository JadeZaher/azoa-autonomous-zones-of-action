// Account gate and onboarding components. See `src/react/AGENTS.md` §components.
import { useState } from "react";
import type { ReactNode } from "react";
import { describeRefusal, safeRedirectUrl } from "../money/index.js";
import type { AccountGateState } from "../money/index.js";
import { useMoneyContext } from "./context.js";
import type { MoneyEventProps } from "./context.js";
import { formatDateTime, useSlots } from "./internal.js";
import type { MoneySlots } from "./slots.js";
import { useCustodialAccount } from "./use-custodial-account.js";

/** Props every money component takes. */
export interface MoneyComponentProps extends MoneyEventProps {
  slots?: Partial<MoneySlots>;
  className?: string;
}

export interface MoneyGateProps extends MoneyComponentProps {
  /** The state that unlocks `children`; only `ready` may move value. */
  require?: Extract<AccountGateState, "ready">;
  /** Rendered while the account is read. */
  fallback?: ReactNode;
  children?: ReactNode;
}

/** Renders `children` only when the account is ready; otherwise the step that gets it there. */
export function MoneyGate(props: MoneyGateProps) {
  const required = props.require ?? "ready";
  const account = useCustodialAccount(props);
  const { messages } = useMoneyContext();
  const { Alert } = useSlots(props.slots);
  const stepProps: MoneyComponentProps = { slots: props.slots, className: props.className, onRedirect: props.onRedirect, onRefusal: props.onRefusal };
  if (account.gate === required) return <>{props.children}</>;
  switch (account.gate) {
    case "loading":
      return <>{props.fallback ?? <Alert tone="info" className={props.className}>{messages.gate.loading}</Alert>}</>;
    case "setup":
      return <KycStep {...stepProps} />;
    case "review":
      return (
        <Alert tone="warning" className={props.className}>
          {account.refusal?.code === "under_review" ? describeRefusal(account.refusal, messages) : messages.gate.review}
          {account.account?.reviewDueAt ? ` ${messages.gate.reviewDue(formatDateTime(account.account.reviewDueAt))}` : null}
        </Alert>
      );
    case "revoked":
      return <ConsentStatus {...stepProps} />;
    default:
      return (
        <Alert tone="error" className={props.className}>
          {account.refusal ? describeRefusal(account.refusal, messages) : account.account?.unavailableReason ?? messages.gate.unavailable}
        </Alert>
      );
  }
}

/** Identity verification (and account completion) for an account that is not ready yet. */
export function KycStep(props: MoneyComponentProps) {
  const account = useCustodialAccount(props);
  const { messages } = useMoneyContext();
  const { Card, Button, Alert } = useSlots(props.slots);
  const status = account.account?.kycStatus ?? "Unknown";
  const finishSetup = status === "Approved" && account.account !== null && !account.account.walletReady;
  const verificationUrl = safeRedirectUrl(account.kycSession?.verificationUrl);
  return (
    <Card title={messages.kyc.title} description={messages.kycStatus[status]} className={props.className}>
      {account.refusal ? <Alert tone="error">{describeRefusal(account.refusal, messages)}</Alert> : null}
      {status !== "UnderReview" ? (
        <Button onClick={() => void (finishSetup ? account.ensure() : account.beginKyc())} disabled={account.busy}>
          {finishSetup ? messages.kyc.finishSetup : status === "Pending" ? messages.kyc.continue : messages.kyc.start}
        </Button>
      ) : null}
      {verificationUrl && !account.canRedirect ? <a href={verificationUrl}>{messages.kyc.openVerification}</a> : null}
      {account.kycSession?.instructions ? <Alert tone="info">{account.kycSession.instructions}</Alert> : null}
      {account.kycSession?.developmentSimulation ? <Alert tone="info">{messages.kyc.simulation}</Alert> : null}
      <Button variant="secondary" onClick={() => void account.refresh()} disabled={account.busy}>{messages.kyc.checkAgain}</Button>
    </Card>
  );
}

/** Whether this app may manage the person's payments, with a confirmed way to withdraw that permission. */
export function ConsentStatus(props: MoneyComponentProps) {
  const account = useCustodialAccount(props);
  const { messages } = useMoneyContext();
  const { Card, Button, Alert } = useSlots(props.slots);
  const [confirming, setConfirming] = useState(false);
  const revoked = account.gate === "revoked";
  return (
    <Card title={messages.consent.title} description={revoked ? messages.consent.revoked : messages.consent.active} className={props.className}>
      {account.refusal && account.refusal.code !== "consent_revoked" ? <Alert tone="error">{describeRefusal(account.refusal, messages)}</Alert> : null}
      {!revoked && !confirming ? <Button variant="secondary" onClick={() => setConfirming(true)}>{messages.consent.revoke}</Button> : null}
      {!revoked && confirming ? (
        <>
          <p>{messages.consent.confirmPrompt}</p>
          <Button onClick={() => void account.revokeConsent().then(() => setConfirming(false))} disabled={account.busy}>{messages.consent.confirmRevoke}</Button>
          <Button variant="secondary" onClick={() => setConfirming(false)} disabled={account.busy}>{messages.consent.keep}</Button>
        </>
      ) : null}
    </Card>
  );
}

/** A read-only summary of the account: identity, wallet and where the gate stands. */
export function CustodialAccountCard(props: MoneyComponentProps) {
  const account = useCustodialAccount(props);
  const { messages } = useMoneyContext();
  const { Card } = useSlots(props.slots);
  const view = account.account;
  return (
    <Card title={messages.account.title} description={messages.gate[account.gate]} className={props.className}>
      {view ? (
        <dl>
          <dt>{messages.account.identityLabel}</dt>
          <dd>{messages.kycStatusLabel[view.kycStatus]}</dd>
          <dt>{messages.account.walletLabel}</dt>
          <dd>{view.walletReady ? messages.account.walletReady : messages.account.walletPending}</dd>
          {view.walletAddress ? (
            <>
              <dt>{messages.account.addressLabel}</dt>
              <dd>{view.walletAddress}</dd>
            </>
          ) : null}
        </dl>
      ) : null}
    </Card>
  );
}
