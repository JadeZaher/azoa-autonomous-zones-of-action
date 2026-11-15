// Standalone- and provider-compatible consumer ride-along components for commercial & access primitives.
import { useId, useMemo, useState } from "react";
import {
  ACCESS_LINK_SCOPES,
  ACCOUNT_DOCUMENT_TYPES,
  formatInvoiceTotal,
  isAccessLinkExpired,
  toMinorUnits,
} from "../money/index.js";
import type {
  AccessLinkScope,
  AccountDocumentReportView,
  AccountDocumentType,
  CommercialInvoiceView,
  IdentomatSessionStatusView,
  OwnershipHolderClass,
  OwnershipReceiptView,
  PaymentRequestView,
  ProviderDeferralView,
  SignedAccessLinkView,
} from "../money/index.js";
import { useOptionalMoneyContext } from "./context.js";
import { defaultMoneySlots } from "./slots.js";
import type { MoneySlots } from "./slots.js";

const SCOPE_LABELS: Record<AccessLinkScope, string> = {
  kyc: "Identity verification (kyc)",
  consent: "Account consent (consent)",
  add_funds: "Add funds (add_funds)",
  cash_out: "Cash out (cash_out)",
  invoice_pay: "Invoice payment (invoice_pay)",
  receipt_view: "Receipt view (receipt_view)",
  statement_view: "Statement view (statement_view)",
};

const DOCUMENT_TYPE_LABELS: Record<AccountDocumentType, string> = {
  monthly_statement: "Monthly Statement",
  payout_ledger_report: "Payout Ledger Report",
  ownership_certificate: "Ownership Certificate",
  kyc_consent_attestation: "KYC & Consent Attestation",
  tax_activity_summary: "Tax Activity Summary",
};

const HOLDER_CLASSES: readonly OwnershipHolderClass[] = [
  "MEMBER",
  "CONTRIBUTOR",
  "INVESTOR",
  "FOUNDER",
];

function useCommercialSlots(local?: Partial<MoneySlots>): MoneySlots {
  const context = useOptionalMoneyContext();
  const base = context?.slots ?? defaultMoneySlots;
  return local ? { ...base, ...local } : base;
}

function formatIsoTimestamp(iso: string | null | undefined): string {
  if (!iso) return "—";
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

function shortDigest(hash: string): string {
  const trimmed = hash.trim();
  if (trimmed.length <= 16) return trimmed;
  return `${trimmed.slice(0, 10)}…${trimmed.slice(-6)}`;
}

function safeCommercialHref(url: string | null | undefined): string | null {
  if (typeof url !== "string") return null;
  const trimmed = url.trim();
  if (!trimmed) return null;
  if (trimmed.startsWith("/") && !trimmed.startsWith("//")) return trimmed;
  try {
    const parsed = new URL(trimmed);
    if (parsed.username || parsed.password) return null;
    if (parsed.protocol === "https:") return parsed.href;
    if (
      parsed.protocol === "http:" &&
      (parsed.hostname === "localhost" || parsed.hostname === "127.0.0.1" || parsed.hostname === "[::1]")
    ) {
      return parsed.href;
    }
    return null;
  } catch {
    return null;
  }
}

function triggerJsonDownload(filename: string, data: unknown): void {
  if (typeof document === "undefined" || typeof URL === "undefined" || typeof URL.createObjectURL !== "function") {
    return;
  }
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
  const objectUrl = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = objectUrl;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  URL.revokeObjectURL(objectUrl);
}

// ─── 1. AccessLinkRideAlongCard ───

export interface AccessLinkMintInput {
  scope: AccessLinkScope;
  targetResourceId?: string | null;
  returnUrl?: string | null;
}

export interface AccessLinkRideAlongCardProps {
  link?: SignedAccessLinkView | null;
  scopes?: readonly AccessLinkScope[];
  onMint?: (input: AccessLinkMintInput) => void | Promise<void>;
  onOpenRideAlong?: (url: string, link: SignedAccessLinkView) => void;
  slots?: Partial<MoneySlots>;
  busy?: boolean;
  className?: string;
  mintButtonLabel?: string;
}

/** Signed ride-along access link issuer and launcher supporting all seven AZOA capability scopes. */
export function AccessLinkRideAlongCard(props: AccessLinkRideAlongCardProps) {
  const { Button, Input, Card, Alert } = useCommercialSlots(props.slots);
  const availableScopes = props.scopes && props.scopes.length > 0 ? props.scopes : ACCESS_LINK_SCOPES;
  const [scope, setScope] = useState<AccessLinkScope>(availableScopes[0] ?? "kyc");
  const [targetResourceId, setTargetResourceId] = useState("");
  const [returnUrl, setReturnUrl] = useState("");
  const [copied, setCopied] = useState(false);

  const scopeId = useId();
  const resourceInputId = useId();
  const returnUrlInputId = useId();
  const signedUrlInputId = useId();

  const expired = props.link ? isAccessLinkExpired(props.link.expiresAt) : false;
  const safeSignedUrl = props.link ? safeCommercialHref(props.link.signedUrl) : null;

  const handleMint = () => {
    if (!props.onMint || props.busy) return;
    setCopied(false);
    void props.onMint({
      scope,
      targetResourceId: targetResourceId.trim() || null,
      returnUrl: returnUrl.trim() || null,
    });
  };

  const handleCopy = async () => {
    if (!props.link) return;
    if (typeof navigator !== "undefined" && navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(props.link.signedUrl);
        setCopied(true);
        return;
      } catch {
        // Clipboard permission denied; keep copied false.
      }
    }
    setCopied(false);
  };

  const handleOpen = () => {
    if (!props.link || !safeSignedUrl || expired) return;
    if (props.onOpenRideAlong) {
      props.onOpenRideAlong(safeSignedUrl, props.link);
      return;
    }
    if (typeof window !== "undefined") {
      window.open(safeSignedUrl, "_blank", "noopener,noreferrer");
    }
  };

  return (
    <Card title="Signed Access Link" className={props.className}>
      <form
        onSubmit={event => {
          event.preventDefault();
          handleMint();
        }}
      >
        <div>
          <label htmlFor={scopeId}>Scope</label>
          <select
            id={scopeId}
            value={scope}
            onChange={event => setScope(event.target.value as AccessLinkScope)}
            disabled={props.busy}
          >
            {availableScopes.map(entry => (
              <option key={entry} value={entry}>
                {SCOPE_LABELS[entry]}
              </option>
            ))}
          </select>
        </div>

        <div>
          <label htmlFor={resourceInputId}>Target resource ID (optional)</label>
          <Input
            id={resourceInputId}
            value={targetResourceId}
            onChange={event => setTargetResourceId(event.target.value)}
            placeholder="inv_123 or rcp_456"
            disabled={props.busy}
          />
        </div>

        <div>
          <label htmlFor={returnUrlInputId}>Return URL (optional)</label>
          <Input
            id={returnUrlInputId}
            value={returnUrl}
            onChange={event => setReturnUrl(event.target.value)}
            placeholder="https://partner.example/callback"
            disabled={props.busy}
          />
        </div>

        <Button type="submit" disabled={props.busy || !props.onMint}>
          {props.mintButtonLabel ?? "Mint Access Link"}
        </Button>
      </form>

      {props.link ? (
        <div data-testid="signed-access-link-result">
          <div>
            <span data-status={expired ? "expired" : "active"}>
              {expired ? "Expired" : "Active"}
            </span>
            <span> · Scope: {props.link.scope}</span>
            <span> · Expires: {formatIsoTimestamp(props.link.expiresAt)}</span>
            {props.link.deferralStatus !== "active" ? (
              <span> · Mode: {props.link.deferralStatus}</span>
            ) : null}
          </div>

          {props.link.deferredReason ? (
            <Alert tone="warning">{props.link.deferredReason}</Alert>
          ) : null}

          <div>
            <label htmlFor={signedUrlInputId}>Signed Ride URL</label>
            <Input
              id={signedUrlInputId}
              value={props.link.signedUrl}
              onChange={() => undefined}
              disabled={false}
            />
            <Button type="button" variant="secondary" onClick={() => void handleCopy()} disabled={props.busy}>
              {copied ? "Copied" : "Copy URL"}
            </Button>
            <Button
              type="button"
              variant="secondary"
              onClick={handleOpen}
              disabled={props.busy || expired || !safeSignedUrl}
            >
              Open Hosted Ride
            </Button>
          </div>
        </div>
      ) : null}
    </Card>
  );
}

// ─── 2. IdentomatVerificationRide ───

export interface IdentomatVerificationRideProps {
  session?: IdentomatSessionStatusView | null;
  onStartVerification?: () => void | Promise<void>;
  onRefresh?: () => void | Promise<void>;
  slots?: Partial<MoneySlots>;
  busy?: boolean;
  className?: string;
}

/** Renders Identomat KYC/KYB verification state, hosted launch link, and graceful credential deferral notice. */
export function IdentomatVerificationRide(props: IdentomatVerificationRideProps) {
  const { Button, Card, Alert } = useCommercialSlots(props.slots);
  const session = props.session ?? null;
  const status = session?.status ?? null;
  const launchUrl = safeCommercialHref(session?.verificationUrl ?? session?.signedAccessUrl);

  return (
    <Card title="Identomat Verification" className={props.className}>
      {status === "Approved" ? (
        <Alert tone="success">Identity verification approved by Identomat.</Alert>
      ) : null}
      {status === "Rejected" ? (
        <Alert tone="error">Identity verification was not approved. Start a new session to try again.</Alert>
      ) : null}
      {status === "UnderReview" ? (
        <Alert tone="info">Verification submitted and currently under compliance review.</Alert>
      ) : null}
      {status === "Pending" ? (
        <Alert tone="info">Verification session in progress. Complete verification and refresh status.</Alert>
      ) : null}
      {status === "PendingVendorCredentials" ? (
        <Alert tone="warning">
          <strong>Vendor credentials queued (PendingVendorCredentials)</strong>
          {": "}
          {session?.deferredReason ??
            "Identomat credentials are queued for this deployment. Your verification request is recorded and ready to resume once active."}
          {session?.deferralLoggedAt ? ` Logged at ${formatIsoTimestamp(session.deferralLoggedAt)}.` : ""}
        </Alert>
      ) : null}
      {!status ? <p>No active Identomat session yet.</p> : null}

      {session?.expiresAt ? <p>Session expires: {formatIsoTimestamp(session.expiresAt)}</p> : null}

      <div>
        {status !== "Approved" && props.onStartVerification ? (
          <Button onClick={() => void props.onStartVerification?.()} disabled={props.busy}>
            {status === "Pending" ? "Resume Verification" : "Start Verification"}
          </Button>
        ) : null}
        {props.onRefresh ? (
          <Button variant="secondary" onClick={() => void props.onRefresh?.()} disabled={props.busy}>
            Refresh Status
          </Button>
        ) : null}
        {launchUrl ? (
          <a href={launchUrl} target="_blank" rel="noopener noreferrer">
            Launch Identomat Verification
          </a>
        ) : null}
      </div>
    </Card>
  );
}

// ─── 3. InvoiceAndPaymentRequestPanel ───

export interface CreateInvoiceDraft {
  title: string;
  payerSubject: string;
  currencyCode: string;
  description: string;
  quantity: number;
  unitPriceMinorUnits: number;
  feeMinorUnits?: number;
}

export interface CreatePaymentRequestDraft {
  title: string;
  payerSubject: string;
  currencyCode: string;
  amountMinorUnits: number;
  memo?: string;
}

export interface InvoiceAndPaymentRequestPanelProps {
  invoices: readonly CommercialInvoiceView[];
  paymentRequests: readonly PaymentRequestView[];
  onCreateInvoice?: (draft: CreateInvoiceDraft) => void | Promise<void>;
  onCreatePaymentRequest?: (draft: CreatePaymentRequestDraft) => void | Promise<void>;
  onPayInvoice?: (invoiceId: string) => void | Promise<void>;
  onVoidInvoice?: (invoiceId: string) => void | Promise<void>;
  onPayPaymentRequest?: (requestId: string) => void | Promise<void>;
  slots?: Partial<MoneySlots>;
  busy?: boolean;
  className?: string;
}

/** Commercial invoice & shareable payment request table with quick builders and pay/void actions. */
export function InvoiceAndPaymentRequestPanel(props: InvoiceAndPaymentRequestPanelProps) {
  const { Button, Input, Card, Alert } = useCommercialSlots(props.slots);
  const [composer, setComposer] = useState<"none" | "invoice" | "request">("none");
  const [validationError, setValidationError] = useState<string | null>(null);

  const [invTitle, setInvTitle] = useState("");
  const [invPayer, setInvPayer] = useState("");
  const [invCurrency, setInvCurrency] = useState("USD");
  const [invDescription, setInvDescription] = useState("");
  const [invQuantity, setInvQuantity] = useState("1");
  const [invUnitPrice, setInvUnitPrice] = useState("");

  const [reqTitle, setReqTitle] = useState("");
  const [reqPayer, setReqPayer] = useState("");
  const [reqCurrency, setReqCurrency] = useState("USD");
  const [reqAmount, setReqAmount] = useState("");
  const [reqMemo, setReqMemo] = useState("");

  const invTitleId = useId();
  const invPayerId = useId();
  const invCurrencyId = useId();
  const invDescId = useId();
  const invQtyId = useId();
  const invPriceId = useId();

  const reqTitleId = useId();
  const reqPayerId = useId();
  const reqCurrencyId = useId();
  const reqAmountId = useId();
  const reqMemoId = useId();

  const submitInvoice = () => {
    if (!props.onCreateInvoice || props.busy) return;
    const payerSubject = invPayer.trim();
    const description = invDescription.trim() || invTitle.trim() || "Commercial service";
    const quantity = Number.parseInt(invQuantity, 10);
    const unitPriceMinorUnits = toMinorUnits(invUnitPrice, invCurrency);
    if (!payerSubject) {
      setValidationError("Enter a payer subject.");
      return;
    }
    if (!Number.isInteger(quantity) || quantity < 1) {
      setValidationError("Quantity must be a whole number of at least 1.");
      return;
    }
    if (unitPriceMinorUnits === null || unitPriceMinorUnits <= 0) {
      setValidationError("Enter a valid unit price for the selected currency.");
      return;
    }
    setValidationError(null);
    void props.onCreateInvoice({
      title: invTitle.trim() || description,
      payerSubject,
      currencyCode: invCurrency.trim().toUpperCase(),
      description,
      quantity,
      unitPriceMinorUnits,
    });
    setComposer("none");
    setInvTitle("");
    setInvDescription("");
    setInvUnitPrice("");
  };

  const submitPaymentRequest = () => {
    if (!props.onCreatePaymentRequest || props.busy) return;
    const payerSubject = reqPayer.trim();
    const amountMinorUnits = toMinorUnits(reqAmount, reqCurrency);
    if (!payerSubject) {
      setValidationError("Enter a payer subject.");
      return;
    }
    if (amountMinorUnits === null || amountMinorUnits <= 0) {
      setValidationError("Enter a valid request amount for the selected currency.");
      return;
    }
    setValidationError(null);
    void props.onCreatePaymentRequest({
      title: reqTitle.trim() || "Payment request",
      payerSubject,
      currencyCode: reqCurrency.trim().toUpperCase(),
      amountMinorUnits,
      memo: reqMemo.trim() || undefined,
    });
    setComposer("none");
    setReqTitle("");
    setReqAmount("");
    setReqMemo("");
  };

  return (
    <Card title="Invoices & Payment Requests" className={props.className}>
      {validationError ? <Alert tone="error">{validationError}</Alert> : null}

      <div>
        {props.onCreateInvoice ? (
          <Button
            variant={composer === "invoice" ? "secondary" : "primary"}
            onClick={() => {
              setValidationError(null);
              setComposer(composer === "invoice" ? "none" : "invoice");
            }}
            disabled={props.busy}
          >
            {composer === "invoice" ? "Close Invoice Builder" : "New Invoice"}
          </Button>
        ) : null}
        {props.onCreatePaymentRequest ? (
          <Button
            variant="secondary"
            onClick={() => {
              setValidationError(null);
              setComposer(composer === "request" ? "none" : "request");
            }}
            disabled={props.busy}
          >
            {composer === "request" ? "Close Request Builder" : "New Payment Request"}
          </Button>
        ) : null}
      </div>

      {composer === "invoice" ? (
        <form
          onSubmit={event => {
            event.preventDefault();
            submitInvoice();
          }}
        >
          <div>
            <label htmlFor={invTitleId}>Invoice title</label>
            <Input id={invTitleId} value={invTitle} onChange={event => setInvTitle(event.target.value)} placeholder="Advisory retainer" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={invPayerId}>Payer subject</label>
            <Input id={invPayerId} value={invPayer} onChange={event => setInvPayer(event.target.value)} placeholder="member-123" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={invCurrencyId}>Currency</label>
            <Input id={invCurrencyId} value={invCurrency} onChange={event => setInvCurrency(event.target.value)} placeholder="USD" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={invDescId}>Line item description</label>
            <Input id={invDescId} value={invDescription} onChange={event => setInvDescription(event.target.value)} placeholder="Monthly allocation" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={invQtyId}>Quantity</label>
            <Input id={invQtyId} value={invQuantity} onChange={event => setInvQuantity(event.target.value)} inputMode="numeric" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={invPriceId}>Unit price</label>
            <Input id={invPriceId} value={invUnitPrice} onChange={event => setInvUnitPrice(event.target.value)} inputMode="decimal" placeholder="125.00" disabled={props.busy} />
          </div>
          <Button type="submit" disabled={props.busy}>Create Invoice</Button>
        </form>
      ) : null}

      {composer === "request" ? (
        <form
          onSubmit={event => {
            event.preventDefault();
            submitPaymentRequest();
          }}
        >
          <div>
            <label htmlFor={reqTitleId}>Request title</label>
            <Input id={reqTitleId} value={reqTitle} onChange={event => setReqTitle(event.target.value)} placeholder="Settlement deposit" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={reqPayerId}>Payer subject</label>
            <Input id={reqPayerId} value={reqPayer} onChange={event => setReqPayer(event.target.value)} placeholder="member-123" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={reqCurrencyId}>Currency</label>
            <Input id={reqCurrencyId} value={reqCurrency} onChange={event => setReqCurrency(event.target.value)} placeholder="USD" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={reqAmountId}>Amount</label>
            <Input id={reqAmountId} value={reqAmount} onChange={event => setReqAmount(event.target.value)} inputMode="decimal" placeholder="50.00" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={reqMemoId}>Memo (optional)</label>
            <Input id={reqMemoId} value={reqMemo} onChange={event => setReqMemo(event.target.value)} placeholder="Reference note" disabled={props.busy} />
          </div>
          <Button type="submit" disabled={props.busy}>Create Payment Request</Button>
        </form>
      ) : null}

      <div>
        <h4>Commercial Invoices</h4>
        {props.invoices.length === 0 ? (
          <p>No commercial invoices yet.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th scope="col">Invoice</th>
                <th scope="col">Payer</th>
                <th scope="col">Total</th>
                <th scope="col">Status</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {props.invoices.map(invoice => {
                const actionable = invoice.status === "open" || invoice.status === "draft";
                const payHref = safeCommercialHref(invoice.paymentLinkUrl);
                return (
                  <tr key={invoice.id}>
                    <td>
                      <div>{invoice.invoiceNumber}</div>
                      {invoice.title ? <div>{invoice.title}</div> : null}
                    </td>
                    <td>{invoice.payerSubject}</td>
                    <td>{formatInvoiceTotal(invoice.totalMinorUnits, invoice.currencyCode)}</td>
                    <td>{invoice.status}</td>
                    <td>
                      {actionable && props.onPayInvoice ? (
                        <Button onClick={() => void props.onPayInvoice?.(invoice.id)} disabled={props.busy}>
                          Pay Invoice
                        </Button>
                      ) : null}
                      {actionable && props.onVoidInvoice ? (
                        <Button variant="secondary" onClick={() => void props.onVoidInvoice?.(invoice.id)} disabled={props.busy}>
                          Void
                        </Button>
                      ) : null}
                      {payHref ? (
                        <a href={payHref} target="_blank" rel="noopener noreferrer">
                          Pay Link
                        </a>
                      ) : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      <div>
        <h4>Payment Requests</h4>
        {props.paymentRequests.length === 0 ? (
          <p>No payment requests yet.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th scope="col">Request</th>
                <th scope="col">Payer</th>
                <th scope="col">Amount</th>
                <th scope="col">Status</th>
                <th scope="col">Share</th>
              </tr>
            </thead>
            <tbody>
              {props.paymentRequests.map(req => {
                const payHref = safeCommercialHref(req.paymentLinkUrl);
                const actionable = req.status === "open" || req.status === "draft";
                return (
                  <tr key={req.id}>
                    <td>
                      <div>{req.requestNumber}</div>
                      {req.title ? <div>{req.title}</div> : null}
                    </td>
                    <td>{req.payerSubject}</td>
                    <td>{formatInvoiceTotal(req.amountMinorUnits, req.currencyCode)}</td>
                    <td>{req.status}</td>
                    <td>
                      {actionable && props.onPayPaymentRequest ? (
                        <Button onClick={() => void props.onPayPaymentRequest?.(req.id)} disabled={props.busy}>
                          Pay Request
                        </Button>
                      ) : null}
                      {payHref ? (
                        <a href={payHref} target="_blank" rel="noopener noreferrer">
                          Open Request Link
                        </a>
                      ) : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </Card>
  );
}

// ─── 4. OwnershipReceiptsPanel ───

export interface IssueReceiptDraft {
  allocationOrSaleId: string;
  holderSubject: string;
  holderClass: OwnershipHolderClass;
  assetSymbol: string;
  unitsFormatted: string;
  fiatMinorUnits: number;
  currencyCode: string;
}

export interface OwnershipReceiptsPanelProps {
  receipts: readonly OwnershipReceiptView[];
  onIssueReceipt?: (draft: IssueReceiptDraft) => void | Promise<void>;
  onInspectReceipt?: (receipt: OwnershipReceiptView) => void;
  slots?: Partial<MoneySlots>;
  busy?: boolean;
  className?: string;
}

/** Verifiable ownership and settlement receipts with SHA-256 content hash, unit formatting, and JSON/print export. */
export function OwnershipReceiptsPanel(props: OwnershipReceiptsPanelProps) {
  const { Button, Input, Card, Alert } = useCommercialSlots(props.slots);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [showIssueForm, setShowIssueForm] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [allocId, setAllocId] = useState("");
  const [holderSubject, setHolderSubject] = useState("");
  const [holderClass, setHolderClass] = useState<OwnershipHolderClass>("MEMBER");
  const [assetSymbol, setAssetSymbol] = useState("UNIT");
  const [unitsFormatted, setUnitsFormatted] = useState("");
  const [fiatAmount, setFiatAmount] = useState("");
  const [currencyCode, setCurrencyCode] = useState("USD");

  const allocInputId = useId();
  const holderInputId = useId();
  const classSelectId = useId();
  const symbolInputId = useId();
  const unitsInputId = useId();
  const amountInputId = useId();
  const currencyInputId = useId();

  const inspectedReceipt = useMemo(
    () => props.receipts.find(receipt => receipt.receiptId === selectedId) ?? null,
    [props.receipts, selectedId],
  );

  const handleIssue = () => {
    if (!props.onIssueReceipt || props.busy) return;
    const minor = toMinorUnits(fiatAmount, currencyCode);
    if (!allocId.trim() || !holderSubject.trim() || !assetSymbol.trim() || !unitsFormatted.trim()) {
      setError("Complete all receipt fields before issuing.");
      return;
    }
    if (minor === null || minor < 0) {
      setError("Enter a valid settlement amount.");
      return;
    }
    setError(null);
    void props.onIssueReceipt({
      allocationOrSaleId: allocId.trim(),
      holderSubject: holderSubject.trim(),
      holderClass,
      assetSymbol: assetSymbol.trim().toUpperCase(),
      unitsFormatted: unitsFormatted.trim(),
      fiatMinorUnits: minor,
      currencyCode: currencyCode.trim().toUpperCase(),
    });
    setShowIssueForm(false);
    setAllocId("");
    setUnitsFormatted("");
    setFiatAmount("");
  };

  return (
    <Card title="Ownership Receipts" className={props.className}>
      {error ? <Alert tone="error">{error}</Alert> : null}

      {props.onIssueReceipt ? (
        <div>
          <Button
            variant={showIssueForm ? "secondary" : "primary"}
            onClick={() => {
              setError(null);
              setShowIssueForm(!showIssueForm);
            }}
            disabled={props.busy}
          >
            {showIssueForm ? "Cancel" : "Issue Ownership Receipt"}
          </Button>
        </div>
      ) : null}

      {showIssueForm ? (
        <form
          onSubmit={event => {
            event.preventDefault();
            handleIssue();
          }}
        >
          <div>
            <label htmlFor={allocInputId}>Allocation or sale ID</label>
            <Input id={allocInputId} value={allocId} onChange={event => setAllocId(event.target.value)} placeholder="alloc_001" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={holderInputId}>Holder subject</label>
            <Input id={holderInputId} value={holderSubject} onChange={event => setHolderSubject(event.target.value)} placeholder="member-123" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={classSelectId}>Holder class</label>
            <select id={classSelectId} value={holderClass} onChange={event => setHolderClass(event.target.value as OwnershipHolderClass)} disabled={props.busy}>
              {HOLDER_CLASSES.map(entry => (
                <option key={entry} value={entry}>{entry}</option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor={symbolInputId}>Asset symbol</label>
            <Input id={symbolInputId} value={assetSymbol} onChange={event => setAssetSymbol(event.target.value)} placeholder="UNIT" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={unitsInputId}>Formatted units</label>
            <Input id={unitsInputId} value={unitsFormatted} onChange={event => setUnitsFormatted(event.target.value)} placeholder="250.00" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={amountInputId}>Fiat settlement amount</label>
            <Input id={amountInputId} value={fiatAmount} onChange={event => setFiatAmount(event.target.value)} inputMode="decimal" placeholder="250.00" disabled={props.busy} />
          </div>
          <div>
            <label htmlFor={currencyInputId}>Currency</label>
            <Input id={currencyInputId} value={currencyCode} onChange={event => setCurrencyCode(event.target.value)} placeholder="USD" disabled={props.busy} />
          </div>
          <Button type="submit" disabled={props.busy}>Record Receipt</Button>
        </form>
      ) : null}

      {props.receipts.length === 0 ? (
        <p>No ownership receipts recorded yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th scope="col">Receipt</th>
              <th scope="col">Holder</th>
              <th scope="col">Units</th>
              <th scope="col">Fiat Value</th>
              <th scope="col">SHA-256</th>
              <th scope="col">Status</th>
              <th scope="col">Actions</th>
            </tr>
          </thead>
          <tbody>
            {props.receipts.map(receipt => (
              <tr key={receipt.receiptId}>
                <td>{receipt.receiptNumber}</td>
                <td>{receipt.holderSubject} ({receipt.holderClass})</td>
                <td>{receipt.unitsFormatted} {receipt.assetSymbol}</td>
                <td>{formatInvoiceTotal(receipt.fiatMinorUnits, receipt.currencyCode ?? "USD")}</td>
                <td>
                  <code title={receipt.contentHash}>{shortDigest(receipt.contentHash)}</code>
                </td>
                <td>{receipt.verificationStatus}</td>
                <td>
                  <Button
                    variant="secondary"
                    onClick={() => {
                      setSelectedId(selectedId === receipt.receiptId ? null : receipt.receiptId);
                      props.onInspectReceipt?.(receipt);
                    }}
                    disabled={props.busy}
                  >
                    {selectedId === receipt.receiptId ? "Hide" : "Inspect"}
                  </Button>
                  <Button
                    variant="secondary"
                    onClick={() => triggerJsonDownload(`${receipt.receiptNumber}.json`, receipt)}
                    disabled={props.busy}
                  >
                    Export JSON
                  </Button>
                  <Button
                    variant="secondary"
                    onClick={() => {
                      if (typeof window !== "undefined" && typeof window.print === "function") {
                        window.print();
                      }
                    }}
                    disabled={props.busy}
                  >
                    Print
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {inspectedReceipt ? (
        <dl data-testid="ownership-receipt-detail">
          <dt>Receipt Number</dt>
          <dd>{inspectedReceipt.receiptNumber}</dd>
          <dt>Allocation / Sale</dt>
          <dd>{inspectedReceipt.allocationOrSaleId}</dd>
          <dt>SHA-256 Content Hash</dt>
          <dd><code>{inspectedReceipt.contentHash}</code></dd>
          <dt>Issued At</dt>
          <dd>{formatIsoTimestamp(inspectedReceipt.issuedAt)}</dd>
          {inspectedReceipt.chainTxHash ? (
            <>
              <dt>Settlement Reference</dt>
              <dd><code>{inspectedReceipt.chainTxHash}</code></dd>
            </>
          ) : null}
        </dl>
      ) : null}
    </Card>
  );
}

// ─── 5. AccountDocumentsAndReportsPanel ───

export interface GenerateAccountDocumentDraft {
  documentType: AccountDocumentType;
  title?: string;
}

export interface AccountDocumentsAndReportsPanelProps {
  documents: readonly AccountDocumentReportView[];
  deferrals?: readonly ProviderDeferralView[];
  onGenerateDocument?: (draft: GenerateAccountDocumentDraft) => void | Promise<void>;
  slots?: Partial<MoneySlots>;
  busy?: boolean;
  className?: string;
}

/** Lists and generates verifiable account statements/reports and displays structured provider deferrals. */
export function AccountDocumentsAndReportsPanel(props: AccountDocumentsAndReportsPanelProps) {
  const { Button, Input, Card, Alert } = useCommercialSlots(props.slots);
  const [documentType, setDocumentType] = useState<AccountDocumentType>("monthly_statement");
  const [customTitle, setCustomTitle] = useState("");
  const typeSelectId = useId();
  const titleInputId = useId();

  const handleGenerate = () => {
    if (!props.onGenerateDocument || props.busy) return;
    void props.onGenerateDocument({
      documentType,
      title: customTitle.trim() || undefined,
    });
    setCustomTitle("");
  };

  return (
    <Card title="Account Documents & Reports" className={props.className}>
      {props.deferrals && props.deferrals.length > 0 ? (
        <div>
          {props.deferrals.map(deferral => (
            <Alert key={deferral.id} tone={deferral.status === "resolved" ? "info" : "warning"}>
              <strong>{deferral.provider}</strong> ({deferral.capability} · {deferral.reasonCode}): {deferral.message}
              {" "}
              [{deferral.status}]
            </Alert>
          ))}
        </div>
      ) : null}

      {props.onGenerateDocument ? (
        <form
          onSubmit={event => {
            event.preventDefault();
            handleGenerate();
          }}
        >
          <div>
            <label htmlFor={typeSelectId}>Document type</label>
            <select
              id={typeSelectId}
              value={documentType}
              onChange={event => setDocumentType(event.target.value as AccountDocumentType)}
              disabled={props.busy}
            >
              {ACCOUNT_DOCUMENT_TYPES.map(entry => (
                <option key={entry} value={entry}>
                  {DOCUMENT_TYPE_LABELS[entry]}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor={titleInputId}>Title (optional)</label>
            <Input
              id={titleInputId}
              value={customTitle}
              onChange={event => setCustomTitle(event.target.value)}
              placeholder={DOCUMENT_TYPE_LABELS[documentType]}
              disabled={props.busy}
            />
          </div>
          <Button type="submit" disabled={props.busy}>
            Generate Document
          </Button>
        </form>
      ) : null}

      {props.documents.length === 0 ? (
        <p>No account documents generated yet.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th scope="col">Title</th>
              <th scope="col">Type</th>
              <th scope="col">Metrics</th>
              <th scope="col">SHA-256</th>
              <th scope="col">Generated</th>
              <th scope="col">Export</th>
            </tr>
          </thead>
          <tbody>
            {props.documents.map(doc => {
              const downloadHref = safeCommercialHref(doc.downloadUrl);
              const metrics = Object.entries(doc.summaryMetrics);
              return (
                <tr key={doc.documentId}>
                  <td>{doc.title}</td>
                  <td>{DOCUMENT_TYPE_LABELS[doc.documentType] ?? doc.documentType}</td>
                  <td>
                    {metrics.length === 0
                      ? "—"
                      : metrics.map(([k, v]) => `${k}: ${String(v ?? "—")}`).join(" · ")}
                  </td>
                  <td>
                    <code title={doc.contentHash}>{shortDigest(doc.contentHash)}</code>
                  </td>
                  <td>{formatIsoTimestamp(doc.generatedAt)}</td>
                  <td>
                    {downloadHref ? (
                      <a href={downloadHref} target="_blank" rel="noopener noreferrer">
                        Download
                      </a>
                    ) : null}
                    <Button
                      variant="secondary"
                      onClick={() => triggerJsonDownload(`${doc.documentType}-${doc.documentId}.json`, doc)}
                      disabled={props.busy}
                    >
                      Export JSON
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </Card>
  );
}
