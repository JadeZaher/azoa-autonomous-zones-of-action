// @vitest-environment jsdom
import React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  ACCESS_LINK_SCOPES,
  ACCOUNT_DOCUMENT_TYPES,
  SdkError,
  SdkErrorCode,
  buildSignedRideAlongUrl,
  createHttpCommercialTransport,
  formatInvoiceTotal,
  isAccessLinkExpired,
  parseSignedRideAlongParams,
} from "../../src/index.js";
import { defaultMoneyMessages } from "../../src/money/index.js";
import type {
  AccountDocumentReportView,
  CommercialInvoiceView,
  IdentomatSessionStatusView,
  OwnershipReceiptView,
  PaymentRequestView,
  ProviderDeferralView,
  SignedAccessLinkView,
} from "../../src/index.js";
import {
  AccessLinkRideAlongCard,
  AccountDocumentsAndReportsPanel,
  AzoaMoneyProvider,
  CustodialAccountCard,
  IdentomatVerificationRide,
  InvoiceAndPaymentRequestPanel,
  KycStep,
  OwnershipReceiptsPanel,
} from "../../src/react/index.js";
import type { MoneySlots } from "../../src/react/index.js";
import { createTenantBff } from "../react/tenant-bff.js";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const SAMPLE_LINK: SignedAccessLinkView = {
  linkId: "lnk_001",
  externalSubject: "member-42",
  scope: "invoice_pay",
  targetResourceId: "inv_009",
  returnUrl: "https://partner.example/callback",
  signedUrl:
    "https://node.example/ride?linkId=lnk_001&scope=invoice_pay&sub=member-42&exp=2099-01-01T00%3A00%3A00.000Z&sig=sig_abc123",
  signature: "sig_abc123",
  expiresAt: "2099-01-01T00:00:00.000Z",
  createdAt: "2026-11-15T10:00:00.000Z",
  provider: "azoa-hmac-v1",
  deferralStatus: "active",
  deferredReason: null,
};

function makeJsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("commercial URL, expiry, and currency helpers", () => {
  it("exposes canonical access link scopes and account document types", () => {
    expect(ACCESS_LINK_SCOPES).toEqual([
      "kyc",
      "consent",
      "add_funds",
      "cash_out",
      "invoice_pay",
      "receipt_view",
      "statement_view",
    ]);
    expect(ACCOUNT_DOCUMENT_TYPES).toEqual([
      "monthly_statement",
      "payout_ledger_report",
      "ownership_certificate",
      "kyc_consent_attestation",
      "tax_activity_summary",
    ]);
  });

  it("builds signed ride-along URLs cleanly across base URL shapes", () => {
    const fromRoot = buildSignedRideAlongUrl("https://node.example/", SAMPLE_LINK);
    expect(fromRoot).toContain("https://node.example/ride?");
    expect(parseSignedRideAlongParams(fromRoot)).toEqual({
      linkId: "lnk_001",
      scope: "invoice_pay",
      sub: "member-42",
      exp: "2099-01-01T00:00:00.000Z",
      sig: "sig_abc123",
      res: null,
      returnUrl: null,
    });

    const alreadyRide = buildSignedRideAlongUrl("https://node.example/ride", SAMPLE_LINK);
    expect(alreadyRide.startsWith("https://node.example/ride?")).toBe(true);

    const relative = buildSignedRideAlongUrl("", SAMPLE_LINK);
    expect(relative.startsWith("/ride?")).toBe(true);
  });

  it("parses URLSearchParams, raw query strings, full URLs, and blanks safely", () => {
    expect(
      parseSignedRideAlongParams(
        "?linkId=l1&scope=kyc&sub=%20&exp=2099-01-01T00:00:00Z&sig=abc&res=sess_1&returnUrl=https%3A%2F%2Fazoa.ardanova.com%2Fdone",
      ),
    ).toEqual({
      linkId: "l1",
      scope: "kyc",
      sub: null,
      exp: "2099-01-01T00:00:00Z",
      sig: "abc",
      res: "sess_1",
      returnUrl: "https://azoa.ardanova.com/done",
    });
    expect(parseSignedRideAlongParams(new URLSearchParams({ linkId: "l2" }))).toEqual({
      linkId: "l2",
      scope: null,
      sub: null,
      exp: null,
      sig: null,
      res: null,
      returnUrl: null,
    });
  });

  it("evaluates access link expiration fail-closed", () => {
    const now = Date.parse("2026-11-15T12:00:00.000Z");
    expect(isAccessLinkExpired("2026-11-15T12:05:00.000Z", now)).toBe(false);
    expect(isAccessLinkExpired(String(Math.floor(Date.parse("2026-11-15T12:05:00.000Z") / 1000)), now)).toBe(false);
    expect(isAccessLinkExpired(String(Math.floor(now / 1000)), now)).toBe(true);
    expect(isAccessLinkExpired("2026-11-15T12:00:00.000Z", now)).toBe(true);
    expect(isAccessLinkExpired("2026-11-15T11:59:59.000Z", now)).toBe(true);
    expect(isAccessLinkExpired("", now)).toBe(true);
    expect(isAccessLinkExpired("not-a-date", now)).toBe(true);
  });

  it("formats invoice minor units with ISO 4217 precision and fallback", () => {
    expect(formatInvoiceTotal(12550, "usd")).toBe("125.50 USD");
    expect(formatInvoiceTotal(5000, "jpy")).toBe("5,000 JPY");
    expect(formatInvoiceTotal(42, "custom")).toBe("42 CUSTOM");
  });
});

describe("createHttpCommercialTransport", () => {
  it("routes all commercial API calls, unwraps envelopes, and attaches Bearer tokens", async () => {
    const calls: Array<{ url: string; method: string; auth: string | null; body: unknown }> = [];
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input.toString();
      const method = init?.method ?? "GET";
      const headers = new Headers(init?.headers);
      const body = init?.body ? JSON.parse(String(init.body)) : null;
      calls.push({ url, method, auth: headers.get("Authorization"), body });

      if (url.endsWith("/api/commercial/access-links") && method === "POST") {
        return makeJsonResponse({ isError: false, result: SAMPLE_LINK });
      }
      if (url.endsWith("/api/commercial/access-links") && method === "GET") {
        return makeJsonResponse({ items: [SAMPLE_LINK] });
      }
      if (url.includes("/verify") && method === "POST") {
        return makeJsonResponse({ valid: true, expired: false, link: SAMPLE_LINK });
      }
      if (url.endsWith("/api/commercial/identomat/status")) {
        return makeJsonResponse({ provider: "Identomat", status: "PendingVendorCredentials", deferralLogged: true });
      }
      if (url.endsWith("/api/commercial/identomat/sessions")) {
        return makeJsonResponse({
          provider: "Identomat",
          status: "Pending",
          verificationUrl: "https://widget.identomat.com/?session_token=s1",
          deferralLogged: false,
        });
      }
      if (url.endsWith("/api/commercial/invoices") && method === "GET") {
        return makeJsonResponse([]);
      }
      if (url.endsWith("/api/commercial/invoices") && method === "POST") {
        return makeJsonResponse({ id: "inv_1", invoiceNumber: "INV-001", status: "open" });
      }
      if (url.endsWith("/api/commercial/invoices/inv_1/pay")) {
        return makeJsonResponse({ id: "inv_1", invoiceNumber: "INV-001", status: "paid" });
      }
      if (url.endsWith("/api/commercial/invoices/inv_1/void")) {
        return makeJsonResponse({ id: "inv_1", invoiceNumber: "INV-001", status: "void" });
      }
      if (url.endsWith("/api/commercial/payment-requests") && method === "GET") {
        return makeJsonResponse([]);
      }
      if (url.endsWith("/api/commercial/payment-requests") && method === "POST") {
        return makeJsonResponse({ id: "req_1", requestNumber: "REQ-001", status: "open" });
      }
      if (url.endsWith("/api/commercial/payment-requests/req_1/pay")) {
        return makeJsonResponse({ id: "req_1", requestNumber: "REQ-001", status: "paid" });
      }
      if (url.endsWith("/api/commercial/receipts") && method === "GET") {
        return makeJsonResponse([]);
      }
      if (url.endsWith("/api/commercial/receipts") && method === "POST") {
        return makeJsonResponse({ receiptId: "rcp_1", receiptNumber: "RCP-001" });
      }
      if (url.endsWith("/api/commercial/documents") && method === "GET") {
        return makeJsonResponse([]);
      }
      if (url.endsWith("/api/commercial/documents") && method === "POST") {
        return makeJsonResponse({ documentId: "doc_1", documentType: "monthly_statement" });
      }
      if (url.endsWith("/api/commercial/deferrals")) {
        return makeJsonResponse([
          {
            id: "def_1",
            provider: "Identomat",
            capability: "kyc",
            reasonCode: "missing_credentials",
            message: "Credentials pending provisioning",
            loggedAt: "2026-11-15T10:00:00Z",
            status: "deferred",
          },
        ]);
      }
      return makeJsonResponse({ message: "Not found" }, 404);
    });

    const transport = createHttpCommercialTransport({
      baseUrl: "https://api.azoa.example/",
      getToken: () => "jwt-token-123",
      fetchFn: fetchMock as unknown as typeof fetch,
    });

    const minted = await transport.mintAccessLink({ scope: "invoice_pay", targetResourceId: "inv_009" });
    expect(minted.linkId).toBe("lnk_001");
    expect(await transport.listAccessLinks()).toHaveLength(1);
    const verified = await transport.verifyAccessLink("lnk_001", { sig: "sig_abc123", scope: "invoice_pay" });
    expect(verified.valid).toBe(true);
    expect((await transport.getIdentomatStatus()).status).toBe("PendingVendorCredentials");
    expect((await transport.startIdentomatSession({ flowKind: "kyc" })).verificationUrl).toContain("identomat.com");
    expect(await transport.listInvoices()).toEqual([]);
    expect((await transport.createInvoice({ payerSubject: "m1", currencyCode: "USD", lineItems: [] })).id).toBe("inv_1");
    expect((await transport.payInvoice("inv_1")).status).toBe("paid");
    expect((await transport.voidInvoice("inv_1")).status).toBe("void");
    expect(await transport.listPaymentRequests()).toEqual([]);
    expect((await transport.createPaymentRequest({ payerSubject: "m1", currencyCode: "USD", amountMinorUnits: 5000 })).id).toBe("req_1");
    expect((await transport.payPaymentRequest("req_1")).status).toBe("paid");
    expect(await transport.listOwnershipReceipts()).toEqual([]);
    expect((await transport.issueOwnershipReceipt({
      allocationOrSaleId: "sale_1",
      holderSubject: "m1",
      holderClass: "MEMBER",
      assetSymbol: "UNIT",
      unitsFormatted: "100",
      fiatMinorUnits: 10000,
    })).receiptId).toBe("rcp_1");
    expect(await transport.listAccountDocuments()).toEqual([]);
    expect((await transport.generateAccountDocument({ documentType: "monthly_statement" })).documentId).toBe("doc_1");
    expect(await transport.listProviderDeferrals()).toHaveLength(1);

    expect(calls.every(c => c.auth === "Bearer jwt-token-123")).toBe(true);
  });

  it("falls back to GET verification on HTTP 405 and maps auth/network errors to SdkError", async () => {
    let callCount = 0;
    const transport405 = createHttpCommercialTransport({
      baseUrl: "https://api.azoa.example",
      getToken: () => null,
      fetchFn: (async (input: RequestInfo | URL, init?: RequestInit) => {
        callCount += 1;
        if (init?.method === "POST") {
          return makeJsonResponse({ error: "Method Not Allowed" }, 405);
        }
        const url = typeof input === "string" ? input : input.toString();
        expect(url).toContain("/api/commercial/access-links/lnk_2/verify?");
        expect(url).toContain("sig=sig_2");
        return makeJsonResponse(SAMPLE_LINK);
      }) as typeof fetch,
    });

    const result = await transport405.verifyAccessLink("lnk_2", { sig: "sig_2", exp: "2099-01-01T00:00:00.000Z" });
    expect(callCount).toBe(2);
    expect(result.valid).toBe(true);
    expect(result.link?.linkId).toBe("lnk_001");

    const transportFlatBackend = createHttpCommercialTransport({
      baseUrl: "https://api.azoa.example",
      getToken: () => null,
      fetchFn: (async () =>
        makeJsonResponse({
          isError: false,
          result: {
            valid: true,
            linkId: "lnk_flat",
            scope: "invoice_pay",
            externalSubject: "user-9",
            expiresAt: "2099-01-01T00:00:00Z",
            provider: "Identomat",
            deferralStatus: "active",
            targetResourceId: "inv_flat",
            returnUrl: "https://azoa.ardanova.com/done",
          },
        })) as typeof fetch,
    });
    const flatVerified = await transportFlatBackend.verifyAccessLink("lnk_flat", {
      sig: "sig_f",
      res: "inv_flat",
      returnUrl: "https://azoa.ardanova.com/done",
    });
    expect(flatVerified.valid).toBe(true);
    expect(flatVerified.link?.targetResourceId).toBe("inv_flat");
    expect(flatVerified.link?.returnUrl).toBe("https://azoa.ardanova.com/done");

    const transport401 = createHttpCommercialTransport({
      baseUrl: "https://api.azoa.example",
      getToken: () => "expired",
      fetchFn: (async () => makeJsonResponse({ message: "Unauthorized" }, 401)) as typeof fetch,
    });
    await expect(transport401.listInvoices()).rejects.toMatchObject({
      code: SdkErrorCode.AUTH_EXPIRED,
      status: 401,
    });

    const transportNetErr = createHttpCommercialTransport({
      baseUrl: "https://api.azoa.example",
      getToken: () => null,
      fetchFn: (async () => {
        throw new TypeError("Failed to fetch");
      }) as typeof fetch,
    });
    try {
      await transportNetErr.getIdentomatStatus();
      throw new Error("Expected network error");
    } catch (err) {
      expect(err).toBeInstanceOf(SdkError);
      expect((err as SdkError).code).toBe(SdkErrorCode.NETWORK_ERROR);
    }
  });
});

describe("commercial React ride-along components", () => {
  it("renders AccessLinkRideAlongCard standalone, validates minting, copies URL, and launches hosted ride", async () => {
    const user = userEvent.setup();
    const onMint = vi.fn();
    const onOpen = vi.fn();
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, "clipboard", {
      value: { writeText },
      configurable: true,
    });

    const { rerender } = render(
      React.createElement(AccessLinkRideAlongCard, {
        onMint,
        onOpenRideAlong: onOpen,
      }),
    );

    await user.selectOptions(screen.getByLabelText("Scope"), "statement_view");
    await user.type(screen.getByLabelText("Target resource ID (optional)"), "stmt_99");
    await user.type(screen.getByLabelText("Return URL (optional)"), "https://partner.example/done");
    await user.click(screen.getByRole("button", { name: "Mint Access Link" }));

    expect(onMint).toHaveBeenCalledWith({
      scope: "statement_view",
      targetResourceId: "stmt_99",
      returnUrl: "https://partner.example/done",
    });

    rerender(
      React.createElement(AccessLinkRideAlongCard, {
        link: SAMPLE_LINK,
        onMint,
        onOpenRideAlong: onOpen,
      }),
    );

    expect(screen.getByText("Active")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Copy URL" }));
    expect(writeText).toHaveBeenCalledWith(SAMPLE_LINK.signedUrl);
    await user.click(screen.getByRole("button", { name: "Open Hosted Ride" }));
    expect(onOpen).toHaveBeenCalledWith(SAMPLE_LINK.signedUrl, SAMPLE_LINK);
  });

  it("renders IdentomatVerificationRide with PendingVendorCredentials deferral and hosted launch link", async () => {
    const user = userEvent.setup();
    const onStart = vi.fn();
    const onRefresh = vi.fn();
    const deferredSession: IdentomatSessionStatusView = {
      provider: "Identomat",
      status: "PendingVendorCredentials",
      deferralLogged: true,
      deferredReason: "Identomat sandbox credentials queued by operator.",
      deferralLoggedAt: "2026-11-15T10:15:00Z",
    };

    const { rerender } = render(
      React.createElement(IdentomatVerificationRide, {
        session: deferredSession,
        onStartVerification: onStart,
        onRefresh,
      }),
    );

    expect(screen.getByText(/Vendor credentials queued \(PendingVendorCredentials\)/)).toBeTruthy();
    expect(screen.getByText(/Identomat sandbox credentials queued by operator/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Start Verification" }));
    expect(onStart).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole("button", { name: "Refresh Status" }));
    expect(onRefresh).toHaveBeenCalledTimes(1);

    rerender(
      React.createElement(IdentomatVerificationRide, {
        session: {
          provider: "Identomat",
          status: "Pending",
          verificationUrl: "https://widget.identomat.com/?session_token=live_1",
          deferralLogged: false,
        },
        onStartVerification: onStart,
      }),
    );

    const link = screen.getByRole("link", { name: "Launch Identomat Verification" });
    expect(link.getAttribute("href")).toBe("https://widget.identomat.com/?session_token=live_1");
  });

  it("renders InvoiceAndPaymentRequestPanel builder, formatting, pay, and void actions", async () => {
    const user = userEvent.setup();
    const onCreateInvoice = vi.fn();
    const onPayInvoice = vi.fn();
    const onVoidInvoice = vi.fn();
    const onCreatePaymentRequest = vi.fn();

    const invoice: CommercialInvoiceView = {
      id: "inv_1",
      invoiceNumber: "INV-2026-001",
      title: "Platform Membership",
      issuerSubject: "tenant-1",
      payerSubject: "member-42",
      currencyCode: "USD",
      subtotalMinorUnits: 10000,
      feeMinorUnits: 0,
      totalMinorUnits: 10000,
      lineItems: [{ description: "Seat", quantity: 1, unitPriceMinorUnits: 10000, amountMinorUnits: 10000 }],
      status: "open",
      createdAt: "2026-11-15T09:00:00Z",
    };
    const request: PaymentRequestView = {
      id: "req_1",
      requestNumber: "REQ-2026-001",
      requesterSubject: "tenant-1",
      payerSubject: "member-42",
      currencyCode: "USD",
      amountMinorUnits: 5000,
      status: "open",
      paymentLinkUrl: "https://checkout.stripe.com/c/pay/cs_test_1",
      createdAt: "2026-11-15T09:05:00Z",
    };

    render(
      React.createElement(InvoiceAndPaymentRequestPanel, {
        invoices: [invoice],
        paymentRequests: [request],
        onCreateInvoice,
        onCreatePaymentRequest,
        onPayInvoice,
        onVoidInvoice,
      }),
    );

    expect(screen.getByText("100.00 USD")).toBeTruthy();
    expect(screen.getByText("50.00 USD")).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Pay Invoice" }));
    expect(onPayInvoice).toHaveBeenCalledWith("inv_1");
    await user.click(screen.getByRole("button", { name: "Void" }));
    expect(onVoidInvoice).toHaveBeenCalledWith("inv_1");

    await user.click(screen.getByRole("button", { name: "New Invoice" }));
    await user.type(screen.getByLabelText("Invoice title"), "Compliance Fee");
    await user.type(screen.getByLabelText("Payer subject"), "member-99");
    await user.type(screen.getByLabelText("Unit price"), "25.00");
    await user.click(screen.getByRole("button", { name: "Create Invoice" }));

    expect(onCreateInvoice).toHaveBeenCalledWith(
      expect.objectContaining({
        title: "Compliance Fee",
        payerSubject: "member-99",
        currencyCode: "USD",
        quantity: 1,
        unitPriceMinorUnits: 2500,
      }),
    );
  });

  it("renders OwnershipReceiptsPanel and AccountDocumentsAndReportsPanel inside AzoaMoneyProvider using custom slots", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ ada: { kycStatus: "PendingVendorCredentials" } });
    const onGenerate = vi.fn();
    const customSlots: Partial<MoneySlots> = {
      Card: ({ title, description, children }) =>
        React.createElement(
          "section",
          { "data-slot": "custom-card" },
          title ? React.createElement("h3", null, title) : null,
          description ? React.createElement("p", null, description) : null,
          children,
        ),
    };

    const receipt: OwnershipReceiptView = {
      receiptId: "rcp_1",
      receiptNumber: "RCP-1001",
      allocationOrSaleId: "sale_77",
      holderSubject: "ada",
      holderClass: "MEMBER",
      assetSymbol: "UNIT",
      unitsFormatted: "250.00",
      fiatMinorUnits: 25000,
      currencyCode: "USD",
      contentHash: "a1b2c3d4e5f60708091011121314151617181920212223242526272829303132",
      issuedAt: "2026-11-15T10:00:00Z",
      verificationStatus: "verified",
    };

    const doc: AccountDocumentReportView = {
      documentId: "doc_1",
      subject: "ada",
      documentType: "ownership_certificate",
      title: "Q4 Ownership Certificate",
      summaryMetrics: { units: "250.00" },
      contentHash: "ffffffff0000111122223333444455556666777788889999aaaabbbbccccdddd",
      generatedAt: "2026-11-15T10:05:00Z",
    };

    const deferral: ProviderDeferralView = {
      id: "def_1",
      provider: "Identomat",
      capability: "hosted_kyc",
      reasonCode: "missing_credentials",
      message: "Live Identomat API keys pending operator upload.",
      loggedAt: "2026-11-15T10:00:00Z",
      status: "deferred",
    };

    render(
      React.createElement(
        AzoaMoneyProvider,
        {
          transport: bff.transport,
          subject: "ada",
          slots: customSlots,
          onRedirect: () => undefined,
        },
        React.createElement(CustodialAccountCard, null),
        React.createElement(KycStep, null),
        React.createElement(OwnershipReceiptsPanel, { receipts: [receipt] }),
        React.createElement(AccountDocumentsAndReportsPanel, {
          documents: [doc],
          deferrals: [deferral],
          onGenerateDocument: onGenerate,
        }),
      ),
    );

    expect(await screen.findByText(defaultMoneyMessages.kycStatusLabel.PendingVendorCredentials)).toBeTruthy();
    expect(screen.getByText(defaultMoneyMessages.kycStatus.PendingVendorCredentials)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: defaultMoneyMessages.kyc.continue }));
    const fallbackLink = await screen.findByRole("link", { name: defaultMoneyMessages.kyc.openVerification });
    expect(fallbackLink.getAttribute("href")).toBe("https://widget.identomat.com/?session_token=ada");
    expect(screen.getByRole("button", { name: defaultMoneyMessages.kyc.copyLink! })).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Inspect" }));
    await waitFor(() => expect(screen.getByTestId("ownership-receipt-detail")).toBeTruthy());

    await user.selectOptions(screen.getByLabelText("Document type"), "tax_activity_summary");
    await user.click(screen.getByRole("button", { name: "Generate Document" }));
    expect(onGenerate).toHaveBeenCalledWith({
      documentType: "tax_activity_summary",
      title: undefined,
    });
    expect(screen.getByText(/Live Identomat API keys pending operator upload/)).toBeTruthy();
  });
});
