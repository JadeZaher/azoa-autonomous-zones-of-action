import { describe, expect, it } from "vitest";
import {
  currencyMinorDigits,
  defaultMoneyMessages,
  describeSettlement,
  formatMinorUnits,
  isAwaitingFundsCheckout,
  isOpenCheckout,
  isSettledCheckout,
  isTerminalCheckout,
  minorUnitsToText,
  safeRedirectUrl,
  toCheckoutVoidView,
  toMinorUnits,
} from "../../src/money/index.js";
import type { CheckoutView } from "../../src/money/index.js";

describe("checkout state: terminal (key may go) is not settled (polling may stop)", () => {
  // [status, settlement, terminal, settled, open, awaiting funds (may be acknowledged)] — pairings the real node produces (FiatRailProvider.ProjectAsync).
  it.each([
    ["awaiting_payment", "not_started", false, false, true, false],
    ["awaiting_payment", "payment_received", false, false, false, false], // paid, webhook not delivered yet
    ["processing", "payment_received", false, false, false, false], // paid at Stripe, the webhook has not closed it: keep polling
    ["awaiting_payment", "payment_failed", true, true, false, false], // page lapsed at Stripe, no webhook yet
    ["processing", "not_started", false, false, false, false], // async method still clearing
    ["complete", "payment_received", true, false, false, true], // webhook marked it complete; funds applied later (or never: M3)
    ["complete", "not_started", true, false, false, false], // complete, but the node's Stripe re-read failed
    ["complete", "funds_applied", true, true, false, false],
    ["complete", "payment_failed", true, true, false, false],
    ["failed", "not_started", true, true, false, false],
    ["expired", "not_started", true, true, false, false],
    ["expired", "payment_failed", true, true, false, false],
    ["failed", "payment_received", true, false, false, false],
    ["starting", "not_started", false, false, false, false], // a claim with no page yet
    ["complete", "funds_landing", true, false, false, false], // a settlement value this SDK does not know is never final
    ["paid_out", "not_started", false, false, false, false], // nor is an unknown status terminal
  ])("%s + %s", (status, settlementState, terminal, settled, open, awaitingFunds) => {
    const intent = { status, settlementState };
    expect(isTerminalCheckout(intent)).toBe(terminal);
    expect(isSettledCheckout(intent)).toBe(settled);
    expect(isOpenCheckout(intent)).toBe(open);
    expect(isAwaitingFundsCheckout(intent)).toBe(awaitingFunds);
  });
});

describe("describeSettlement", () => {
  const copy = defaultMoneyMessages;
  const view = (status: string, settlementState: string) => ({ status, settlementState }) as Pick<CheckoutView, "status" | "settlementState">;

  it.each([
    ["awaiting_payment", "not_started", copy.settlement.not_started],
    ["complete", "not_started", copy.checkout.confirming],
    ["processing", "not_started", copy.checkout.clearing], // ACH submitted, still clearing: never "waiting"
    ["processing", "payment_received", copy.settlement.payment_received],
    ["complete", "payment_received", copy.settlement.payment_received],
    ["complete", "funds_applied", copy.settlement.funds_applied],
    ["expired", "payment_failed", copy.checkout.expired],
    ["expired", "not_started", copy.checkout.expired],
    ["failed", "not_started", copy.settlement.payment_failed],
    ["complete", "funds_landing", copy.checkout.confirming],
  ])("%s + %s", (status, settlementState, sentence) => {
    expect(describeSettlement(view(status, settlementState))).toBe(sentence);
  });

  it("never says it is waiting for a payment once the intent is complete", () => {
    expect(describeSettlement(view("complete", "not_started"))).not.toBe(copy.settlement.not_started);
  });
});

describe("safeRedirectUrl (LOW-6: only the node's provider origins by default)", () => {
  it.each([
    // The built-in list: the hosted pages the node hands out.
    ["https://checkout.stripe.com/c/pay/cs_test_1", {}, "https://checkout.stripe.com/c/pay/cs_test_1"],
    ["https://connect.stripe.com/setup/c/acct_1/abc", {}, "https://connect.stripe.com/setup/c/acct_1/abc"],
    ["https://widget.identomat.com/?session_token=s1", {}, "https://widget.identomat.com/?session_token=s1"],
    ["HTTPS://Checkout.Stripe.com/pay", {}, "https://checkout.stripe.com/pay"], // the parsed href, not the raw string
    // Safe default: any other https origin is refused until the host lists it.
    ["https://evil.example/pay", {}, null],
    ["https://checkout.stripe.com.evil.example/pay", {}, null],
    ["https://pay.example.com/c/pay/cs_1", {}, null],
    ["https://pay.example.com/c/pay/cs_1", { allowedOrigins: ["https://pay.example.com"] }, "https://pay.example.com/c/pay/cs_1"],
    ["https://checkout.stripe.com/pay", { allowedOrigins: ["https://pay.example.com"] }, "https://checkout.stripe.com/pay"], // extends, never replaces
    ["https://evil.example/pay", { allowedOrigins: ["https://pay.example.com"] }, null],
    ["javascript:alert(1)", {}, null],
    ["data:text/html,<script>", {}, null],
    ["http://checkout.stripe.com/pay", {}, null],
    ["http://pay.example.com/c/pay/cs_1", { allowedOrigins: ["http://pay.example.com"] }, null], // a host cannot list plain http
    ["//evil.example/pay", {}, null],
    ["/relative/path", {}, null],
    ["https://checkout.stripe.com@evil.example/pay", {}, null], // userinfo that reads as a trusted host
    ["https://user@checkout.stripe.com/pay", {}, null],
    ["http://localhost:3000/return", {}, null], // loopback http only behind the dev flag
    ["http://localhost:3000/return", { allowLoopbackHttp: true }, "http://localhost:3000/return"],
    ["http://[::1]:3000/return", { allowLoopbackHttp: true }, "http://[::1]:3000/return"],
    ["http://evil.example/pay", { allowLoopbackHttp: true }, null],
  ])("%s with %o", (url, policy, expected) => {
    expect(safeRedirectUrl(url, policy)).toBe(expected);
  });

  it("refuses non-strings a host transport might send", () => {
    expect(safeRedirectUrl(undefined)).toBeNull();
    expect(safeRedirectUrl(42 as unknown as string)).toBeNull();
  });
});

describe("amounts follow the currency's ISO 4217 minor units", () => {
  it.each([
    ["USD", 2], ["usd", 2], ["JPY", 0], ["KRW", 0], ["KWD", 3], ["BHD", 3], ["CLF", 4], ["XYZ", null], ["", null], ["US", null],
  ])("%s has %s minor digits", (code, digits) => {
    expect(currencyMinorDigits(code)).toBe(digits);
  });

  it.each([
    ["25", "USD", 2500],
    ["25.5", "USD", 2550],
    ["25.50", "USD", 2550],
    ["25.505", "USD", null],
    ["2500", "JPY", 2500],
    ["25.00", "JPY", null],
    ["1.234", "KWD", 1234],
    ["1.2345", "KWD", null],
    ["25", "XYZ", null], // an unknown currency accepts no amount at all
    ["25", "", null],
    ["-1", "USD", null],
    ["1e3", "USD", null],
  ])("parses %s %s", (text, code, minorUnits) => {
    expect(toMinorUnits(text, code)).toBe(minorUnits);
  });

  it.each([
    [2550, "usd", "25.50 USD"],
    [123456, "USD", "1,234.56 USD"],
    [2500, "JPY", "2,500 JPY"],
    [1234, "KWD", "1.234 KWD"],
    [-250, "USD", "-2.50 USD"],
    [2500, "XYZ", null],
    [2500, undefined, null],
  ])("formats %i %s", (minorUnits, code, text) => {
    expect(formatMinorUnits(minorUnits, code)).toBe(text);
  });

  it("round-trips through the plain text the amount field parses", () => {
    expect(minorUnitsToText(2550, "USD")).toBe("25.50");
    expect(toMinorUnits(minorUnitsToText(2550, "USD")!, "USD")).toBe(2550);
    expect(minorUnitsToText(2500, "JPY")).toBe("2500");
  });
});

describe("toCheckoutVoidView: only the node's literal outcomes count, and a revealed intent is narrowed", () => {
  const intent = {
    intentId: "8b4f1f2e-2f1d-4c1b-9d4e-6d1c9f3b2a10", admissionKey: "subject:tenant:key", status: "processing", settlementState: "payment_received",
    paymentUrl: null, resumed: true, amountPaidMinorUnits: 2500, currency: "usd", expiresAt: null,
  };
  const { admissionKey: _admissionKey, ...browserSafe } = intent;
  it.each<[string, unknown, unknown]>([
    ["voided with no intent (a tombstone)", { outcome: "voided", intentId: null }, { outcome: "voided", intentId: null }],
    ["voided naming its intent", { outcome: "voided", intentId: intent.intentId, subjectUserId: "u-1" }, { outcome: "voided", intentId: intent.intentId }],
    ["revealed: the admission key never reaches the browser", { outcome: "revealed", intentId: intent.intentId, intent }, { outcome: "revealed", intent: browserSafe }],
    ["a capitalized outcome", { outcome: "Voided", intentId: null }, "throws"],
    ["revealed without an intent", { outcome: "revealed", intentId: intent.intentId }, "throws"],
    ["no outcome", { intentId: null }, "throws"],
  ])("%s", (_label, answer, expected) => {
    const narrow = () => toCheckoutVoidView(answer as never);
    if (expected === "throws") expect(narrow).toThrow();
    else expect(narrow()).toEqual(expected);
  });
});
