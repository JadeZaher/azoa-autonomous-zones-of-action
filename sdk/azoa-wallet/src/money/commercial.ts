// Commercial & access-link primitives, helpers, and HTTP transport. See `contracts/commercial-primitives.v1.json`.
import { SdkError, SdkErrorCode } from "../core/errors.js";
import { formatMinorUnits } from "./presentation.js";

/** Authorized consumer capability scope for a signed ride-along access link. */
export type AccessLinkScope =
  | "kyc"
  | "consent"
  | "add_funds"
  | "cash_out"
  | "invoice_pay"
  | "receipt_view"
  | "statement_view";

/** Canonical list of supported signed access link scopes. */
export const ACCESS_LINK_SCOPES: readonly AccessLinkScope[] = Object.freeze([
  "kyc",
  "consent",
  "add_funds",
  "cash_out",
  "invoice_pay",
  "receipt_view",
  "statement_view",
]);

export type AccessLinkDeferralStatus = "active" | "deferred_credentials" | "sandbox_preview";

export interface SignedAccessLinkView {
  linkId: string;
  externalSubject: string;
  scope: AccessLinkScope;
  targetResourceId?: string | null;
  returnUrl?: string | null;
  signedUrl: string;
  signature: string;
  rideAlongToken?: string;
  expiresAt: string;
  createdAt?: string;
  provider: string;
  deferralStatus: AccessLinkDeferralStatus;
  deferredReason?: string | null;
}

export type IdentomatVerificationState =
  | "Pending"
  | "PendingVendorCredentials"
  | "UnderReview"
  | "Approved"
  | "Rejected";

export interface IdentomatSessionStatusView {
  provider: "Identomat";
  status: IdentomatVerificationState;
  sessionId?: string | null;
  verificationUrl?: string | null;
  signedAccessUrl?: string | null;
  deferralLogged: boolean;
  deferredReason?: string | null;
  deferralLoggedAt?: string | null;
  expiresAt?: string | null;
}

export interface InvoiceLineItem {
  description: string;
  quantity: number;
  unitPriceMinorUnits: number;
  amountMinorUnits: number;
}

export type CommercialInvoiceStatus = "draft" | "open" | "paid" | "void" | "expired";

export interface CommercialInvoiceView {
  id: string;
  invoiceNumber: string;
  title?: string;
  issuerSubject: string;
  payerSubject: string;
  currencyCode: string;
  subtotalMinorUnits: number;
  feeMinorUnits: number;
  totalMinorUnits: number;
  tokenEquivalentUnits?: string | null;
  lineItems: InvoiceLineItem[];
  status: CommercialInvoiceStatus;
  dueAt?: string | null;
  paymentLinkUrl?: string | null;
  createdAt: string;
  paidAt?: string | null;
  voidedAt?: string | null;
}

export interface PaymentRequestView {
  id: string;
  requestNumber: string;
  title?: string;
  requesterSubject: string;
  payerSubject: string;
  currencyCode: string;
  amountMinorUnits: number;
  tokenEquivalentUnits?: string | null;
  memo?: string | null;
  status: CommercialInvoiceStatus;
  dueAt?: string | null;
  paymentLinkUrl?: string | null;
  createdAt: string;
  paidAt?: string | null;
}

export type OwnershipHolderClass = "CONTRIBUTOR" | "INVESTOR" | "FOUNDER" | "MEMBER";
export type OwnershipVerificationStatus = "verified" | "recorded_pending_chain" | "sandbox_simulated";

export interface OwnershipReceiptView {
  receiptId: string;
  receiptNumber: string;
  allocationOrSaleId: string;
  holderSubject: string;
  holderClass: OwnershipHolderClass;
  assetSymbol: string;
  assetResourceId?: string | null;
  unitsFormatted: string;
  fiatMinorUnits: number;
  currencyCode?: string;
  chainTxHash?: string | null;
  contentHash: string;
  issuedAt: string;
  verificationStatus: OwnershipVerificationStatus;
}

export type AccountDocumentType =
  | "monthly_statement"
  | "payout_ledger_report"
  | "ownership_certificate"
  | "kyc_consent_attestation"
  | "tax_activity_summary";

/** Canonical list of account statement and report document types. */
export const ACCOUNT_DOCUMENT_TYPES: readonly AccountDocumentType[] = Object.freeze([
  "monthly_statement",
  "payout_ledger_report",
  "ownership_certificate",
  "kyc_consent_attestation",
  "tax_activity_summary",
]);

export interface AccountDocumentReportView {
  documentId: string;
  subject: string;
  documentType: AccountDocumentType;
  title: string;
  periodStart?: string | null;
  periodEnd?: string | null;
  summaryMetrics: Record<string, string | number | boolean | null>;
  contentHash: string;
  downloadUrl?: string | null;
  generatedAt: string;
}

export interface ProviderDeferralView {
  id: string;
  provider: string;
  capability: string;
  reasonCode: string;
  message: string;
  missingConfigurationKeys?: string[];
  loggedAt: string;
  status: "deferred" | "resolved";
}

export interface MintAccessLinkInput {
  scope: AccessLinkScope;
  externalSubject?: string;
  targetResourceId?: string | null;
  returnUrl?: string | null;
  ttlSeconds?: number;
}

export interface VerifyAccessLinkInput {
  sig?: string | null;
  exp?: string | null;
  scope?: string | null;
  sub?: string | null;
}

export interface AccessLinkVerificationView {
  valid: boolean;
  expired?: boolean;
  reason?: string | null;
  link?: SignedAccessLinkView | null;
}

export interface StartIdentomatSessionInput {
  externalSubject?: string;
  returnUrl?: string | null;
  flowKind?: "kyc" | "kyb";
}

export interface CreateCommercialInvoiceInput {
  title?: string;
  payerSubject: string;
  currencyCode: string;
  lineItems: InvoiceLineItem[];
  feeMinorUnits?: number;
  tokenEquivalentUnits?: string | null;
  dueAt?: string | null;
}

export interface CreatePaymentRequestInput {
  title?: string;
  payerSubject: string;
  currencyCode: string;
  amountMinorUnits: number;
  tokenEquivalentUnits?: string | null;
  memo?: string | null;
  dueAt?: string | null;
}

export interface IssueOwnershipReceiptInput {
  allocationOrSaleId: string;
  holderSubject: string;
  holderClass: OwnershipHolderClass;
  assetSymbol: string;
  assetResourceId?: string | null;
  unitsFormatted: string;
  fiatMinorUnits: number;
  currencyCode?: string;
  chainTxHash?: string | null;
}

export interface GenerateAccountDocumentInput {
  documentType: AccountDocumentType;
  title?: string;
  periodStart?: string | null;
  periodEnd?: string | null;
}

/** Commercial & ride-along API surface backed by `/api/commercial/*`. */
export interface CommercialTransport {
  mintAccessLink(input: MintAccessLinkInput): Promise<SignedAccessLinkView>;
  listAccessLinks(): Promise<SignedAccessLinkView[]>;
  verifyAccessLink(linkId: string, params?: VerifyAccessLinkInput): Promise<AccessLinkVerificationView>;
  getIdentomatStatus(): Promise<IdentomatSessionStatusView>;
  startIdentomatSession(input?: StartIdentomatSessionInput): Promise<IdentomatSessionStatusView>;
  listInvoices(): Promise<CommercialInvoiceView[]>;
  createInvoice(input: CreateCommercialInvoiceInput): Promise<CommercialInvoiceView>;
  payInvoice(invoiceId: string): Promise<CommercialInvoiceView>;
  voidInvoice(invoiceId: string): Promise<CommercialInvoiceView>;
  listPaymentRequests(): Promise<PaymentRequestView[]>;
  createPaymentRequest(input: CreatePaymentRequestInput): Promise<PaymentRequestView>;
  payPaymentRequest(requestId: string): Promise<PaymentRequestView>;
  listOwnershipReceipts(): Promise<OwnershipReceiptView[]>;
  issueOwnershipReceipt(input: IssueOwnershipReceiptInput): Promise<OwnershipReceiptView>;
  listAccountDocuments(): Promise<AccountDocumentReportView[]>;
  generateAccountDocument(input: GenerateAccountDocumentInput): Promise<AccountDocumentReportView>;
  listProviderDeferrals(): Promise<ProviderDeferralView[]>;
}

export interface HttpCommercialTransportOptions {
  baseUrl: string;
  getToken: () => string | null;
  fetchFn?: typeof fetch;
}

export interface SignedRideAlongParams {
  linkId: string | null;
  scope: string | null;
  sub: string | null;
  exp: string | null;
  sig: string | null;
}

/** Builds a signed `/ride` URL carrying link verification parameters. */
export function buildSignedRideAlongUrl(baseUrl: string, link: SignedAccessLinkView): string {
  const trimmed = baseUrl.trim();
  const params = new URLSearchParams({
    linkId: link.linkId,
    scope: link.scope,
    sub: link.externalSubject,
    exp: link.expiresAt,
    sig: link.signature,
  });

  if (!trimmed) {
    return `/ride?${params.toString()}`;
  }

  try {
    const parsed = new URL(trimmed);
    const cleanPath = parsed.pathname.replace(/\/+$/, "");
    parsed.pathname = cleanPath.endsWith("/ride") ? cleanPath : `${cleanPath}/ride`;
    parsed.search = params.toString();
    parsed.hash = "";
    return parsed.toString();
  } catch {
    const withoutQuery = trimmed.split("?")[0]?.replace(/\/+$/, "") ?? "";
    const targetPath = withoutQuery.endsWith("/ride") ? withoutQuery : `${withoutQuery}/ride`;
    return `${targetPath}?${params.toString()}`;
  }
}

function nonEmptyQueryParam(params: URLSearchParams, key: string): string | null {
  const value = params.get(key);
  if (value === null) return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/** Extracts canonical signed ride-along parameters from URLSearchParams, query string, or full URL. */
export function parseSignedRideAlongParams(searchParams: URLSearchParams | string): SignedRideAlongParams {
  let resolved: URLSearchParams;
  if (typeof searchParams === "string") {
    const trimmed = searchParams.trim();
    if (/^https?:\/\//i.test(trimmed)) {
      try {
        resolved = new URL(trimmed).searchParams;
      } catch {
        resolved = new URLSearchParams();
      }
    } else {
      const queryIndex = trimmed.indexOf("?");
      const queryText = queryIndex >= 0 ? trimmed.slice(queryIndex + 1) : trimmed;
      resolved = new URLSearchParams(queryText);
    }
  } else {
    resolved = searchParams;
  }

  return {
    linkId: nonEmptyQueryParam(resolved, "linkId"),
    scope: nonEmptyQueryParam(resolved, "scope"),
    sub: nonEmptyQueryParam(resolved, "sub"),
    exp: nonEmptyQueryParam(resolved, "exp"),
    sig: nonEmptyQueryParam(resolved, "sig"),
  };
}

/** Returns true when `expiresAt` is missing, malformed, or at/before `nowMs` (fail closed). */
export function isAccessLinkExpired(expiresAt: string, nowMs: number = Date.now()): boolean {
  if (typeof expiresAt !== "string" || expiresAt.trim().length === 0) return true;
  const parsedMs = Date.parse(expiresAt);
  if (!Number.isFinite(parsedMs)) return true;
  return parsedMs <= nowMs;
}

/** Formats invoice/payment minor units with ISO 4217 precision or a safe uppercase fallback. */
export function formatInvoiceTotal(minorUnits: number, currencyCode: string): string {
  const formatted = formatMinorUnits(minorUnits, currencyCode);
  if (formatted !== null) return formatted;
  const normalizedCurrency = typeof currencyCode === "string" && currencyCode.trim().length > 0
    ? currencyCode.trim().toUpperCase()
    : "USD";
  return `${minorUnits} ${normalizedCurrency}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function unwrapArray<T>(payload: unknown): T[] {
  if (Array.isArray(payload)) return payload as T[];
  if (isRecord(payload) && Array.isArray(payload.items)) return payload.items as T[];
  return [];
}

function normalizeVerificationResponse(payload: unknown): AccessLinkVerificationView {
  if (!isRecord(payload)) {
    return { valid: false, reason: "Verification returned an unreadable response." };
  }
  if (typeof payload.valid === "boolean") {
    return payload as unknown as AccessLinkVerificationView;
  }
  if (typeof payload.linkId === "string" && typeof payload.expiresAt === "string") {
    const link = payload as unknown as SignedAccessLinkView;
    const expired = isAccessLinkExpired(link.expiresAt);
    return {
      valid: !expired,
      expired,
      reason: expired ? "Access link has expired." : null,
      link,
    };
  }
  return { valid: false, reason: "Access link verification could not be confirmed." };
}

/** Creates an HTTP `CommercialTransport` targeting `/api/commercial/*` REST routes. */
export function createHttpCommercialTransport(options: HttpCommercialTransportOptions): CommercialTransport {
  const baseUrl = options.baseUrl.trim().replace(/\/+$/, "");
  const fetchImpl = options.fetchFn ?? globalThis.fetch;

  if (typeof fetchImpl !== "function") {
    throw new SdkError(SdkErrorCode.INVALID_INPUT, "createHttpCommercialTransport requires a fetch implementation.");
  }

  async function requestJson<T>(method: "GET" | "POST", path: string, body?: unknown): Promise<{ status: number; data: T }> {
    const url = `${baseUrl}${path}`;
    const headers: Record<string, string> = { Accept: "application/json" };
    const token = options.getToken();
    if (typeof token === "string" && token.trim().length > 0) {
      headers.Authorization = `Bearer ${token.trim()}`;
    }
    if (body !== undefined) {
      headers["Content-Type"] = "application/json";
    }

    let response: Response;
    try {
      response = await fetchImpl(url, {
        method,
        headers,
        ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
      });
    } catch (error) {
      throw new SdkError(SdkErrorCode.NETWORK_ERROR, `${method} ${path} failed due to a network error.`, {
        method,
        path,
        cause: error instanceof Error ? error : new Error(String(error)),
      });
    }

    const rawText = await response.text();
    let parsed: unknown = null;
    if (rawText.trim().length > 0) {
      try {
        parsed = JSON.parse(rawText) as unknown;
      } catch {
        parsed = null;
      }
    }

    if (!response.ok) {
      const errorRecord = isRecord(parsed) ? parsed : {};
      const serverCode = typeof errorRecord.code === "string" ? errorRecord.code : undefined;
      const serverMessage =
        typeof errorRecord.message === "string"
          ? errorRecord.message
          : typeof errorRecord.error === "string"
            ? errorRecord.error
            : typeof errorRecord.title === "string"
              ? errorRecord.title
              : undefined;
      const code = response.status === 401 ? SdkErrorCode.AUTH_EXPIRED : SdkErrorCode.API_ERROR;
      const message = serverMessage
        ? `${method} ${path}: ${serverMessage}`
        : `${method} ${path} failed with HTTP ${response.status}`;
      throw new SdkError(code, message, {
        status: response.status,
        method,
        path,
        serverCode,
        serverMessage,
      });
    }

    if (isRecord(parsed) && typeof parsed.isError === "boolean") {
      if (parsed.isError) {
        const serverCode = typeof parsed.code === "string" ? parsed.code : undefined;
        const serverMessage =
          typeof parsed.message === "string"
            ? parsed.message
            : typeof parsed.error === "string"
              ? parsed.error
              : undefined;
        throw new SdkError(SdkErrorCode.API_ERROR, serverMessage ?? `${method} ${path} failed.`, {
          status: response.status,
          method,
          path,
          serverCode,
          serverMessage,
        });
      }
      return { status: response.status, data: parsed.result as T };
    }

    return { status: response.status, data: parsed as T };
  }

  return {
    async mintAccessLink(input) {
      const { data } = await requestJson<SignedAccessLinkView>("POST", "/api/commercial/access-links", input);
      return data;
    },
    async listAccessLinks() {
      const { data } = await requestJson<unknown>("GET", "/api/commercial/access-links");
      return unwrapArray<SignedAccessLinkView>(data);
    },
    async verifyAccessLink(linkId, params) {
      const encodedId = encodeURIComponent(linkId);
      const path = `/api/commercial/access-links/${encodedId}/verify`;
      const payload = {
        sig: params?.sig ?? null,
        signature: params?.sig ?? null,
        exp: params?.exp ?? null,
        scope: params?.scope ?? null,
        sub: params?.sub ?? null,
      };
      try {
        const { data } = await requestJson<unknown>("POST", path, payload);
        return normalizeVerificationResponse(data);
      } catch (error) {
        if (error instanceof SdkError && error.status === 405) {
          const qs = new URLSearchParams();
          if (params?.sig) qs.set("sig", params.sig);
          if (params?.exp) qs.set("exp", params.exp);
          if (params?.scope) qs.set("scope", params.scope);
          if (params?.sub) qs.set("sub", params.sub);
          const query = qs.toString();
          const { data } = await requestJson<unknown>("GET", query ? `${path}?${query}` : path);
          return normalizeVerificationResponse(data);
        }
        throw error;
      }
    },
    async getIdentomatStatus() {
      const { data } = await requestJson<IdentomatSessionStatusView>("GET", "/api/commercial/identomat/status");
      return data;
    },
    async startIdentomatSession(input) {
      const { data } = await requestJson<IdentomatSessionStatusView>("POST", "/api/commercial/identomat/sessions", input ?? {});
      return data;
    },
    async listInvoices() {
      const { data } = await requestJson<unknown>("GET", "/api/commercial/invoices");
      return unwrapArray<CommercialInvoiceView>(data);
    },
    async createInvoice(input) {
      const { data } = await requestJson<CommercialInvoiceView>("POST", "/api/commercial/invoices", input);
      return data;
    },
    async payInvoice(invoiceId) {
      const { data } = await requestJson<CommercialInvoiceView>(
        "POST",
        `/api/commercial/invoices/${encodeURIComponent(invoiceId)}/pay`,
        {},
      );
      return data;
    },
    async voidInvoice(invoiceId) {
      const { data } = await requestJson<CommercialInvoiceView>(
        "POST",
        `/api/commercial/invoices/${encodeURIComponent(invoiceId)}/void`,
        {},
      );
      return data;
    },
    async listPaymentRequests() {
      const { data } = await requestJson<unknown>("GET", "/api/commercial/payment-requests");
      return unwrapArray<PaymentRequestView>(data);
    },
    async createPaymentRequest(input) {
      const { data } = await requestJson<PaymentRequestView>("POST", "/api/commercial/payment-requests", input);
      return data;
    },
    async payPaymentRequest(requestId) {
      const { data } = await requestJson<PaymentRequestView>(
        "POST",
        `/api/commercial/payment-requests/${encodeURIComponent(requestId)}/pay`,
        {},
      );
      return data;
    },
    async listOwnershipReceipts() {
      const { data } = await requestJson<unknown>("GET", "/api/commercial/receipts");
      return unwrapArray<OwnershipReceiptView>(data);
    },
    async issueOwnershipReceipt(input) {
      const { data } = await requestJson<OwnershipReceiptView>("POST", "/api/commercial/receipts", input);
      return data;
    },
    async listAccountDocuments() {
      const { data } = await requestJson<unknown>("GET", "/api/commercial/documents");
      return unwrapArray<AccountDocumentReportView>(data);
    },
    async generateAccountDocument(input) {
      const { data } = await requestJson<AccountDocumentReportView>("POST", "/api/commercial/documents", input);
      return data;
    },
    async listProviderDeferrals() {
      const { data } = await requestJson<unknown>("GET", "/api/commercial/deferrals");
      return unwrapArray<ProviderDeferralView>(data);
    },
  };
}
