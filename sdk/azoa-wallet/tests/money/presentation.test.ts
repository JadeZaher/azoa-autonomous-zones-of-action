import { describe, expect, it } from "vitest";
import {
  currencyMinorDigits,
  defaultMoneyMessages,
  describeSettlement,
  formatMinorUnits,
  isOpenCheckout,
  isSettledCheckout,
  isTerminalCheckout,
  minorUnitsToText,
  safeRedirectUrl,
  toMinorUnits,
} from "../../src/money/index.js";
import type { CheckoutView } from "../../src/money/index.js";

describe("checkout state: terminal (key may go) is not settled (polling may stop)", () => {
  // [status, settlement, terminal, settled, open] — pairings the real node produces (FiatRailProvider.ProjectAsync).
  it.each([
    ["awaiting_payment", "not_started", false, false, true],
    ["awaiting_payment", "payment_received", false, false, false], // paid, webhook not delivered yet
    ["awaiting_payment", "payment_failed", true, true, false], // page lapsed at Stripe, no webhook yet
    ["processing", "not_started", false, false, false], // async method still clearing
    ["complete", "payment_received", true, false, false], // webhook marked it complete; funds applied later
    ["complete", "not_started", true, false, false], // complete, but the node's Stripe re-read failed
    ["complete", "funds_applied", true, true, false],
    ["complete", "payment_failed", true, true, false],
    ["failed", "not_started", true, true, false],
    ["expired", "not_started", true, true, false],
    ["expired", "payment_failed", true, true, false],
    ["failed", "payment_received", true, false, false],
    ["complete", "funds_landing", true, false, false], // a settlement value this SDK does not know is never final
    ["paid_out", "not_started", false, false, false], // nor is an unknown status terminal
  ])("%s + %s", (status, settlementState, terminal, settled, open) => {
    const intent = { status, settlementState };
    expect(isTerminalCheckout(intent)).toBe(terminal);
    expect(isSettledCheckout(intent)).toBe(settled);
    expect(isOpenCheckout(intent)).toBe(open);
  });
});

describe("describeSettlement", () => {
  const copy = defaultMoneyMessages;
  const view = (status: string, settlementState: string) => ({ status, settlementState }) as Pick<CheckoutView, "status" | "settlementState">;

  it.each([
    ["awaiting_payment", "not_started", copy.settlement.not_started],
    ["complete", "not_started", copy.checkout.confirming],
    ["processing", "not_started", copy.checkout.confirming],
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

describe("safeRedirectUrl", () => {
  it.each([
    ["https://checkout.stripe.com/pay/cs_1", {}, "https://checkout.stripe.com/pay/cs_1"],
    ["HTTPS://Checkout.Stripe.com/pay", {}, "https://checkout.stripe.com/pay"], // the parsed href, not the raw string
    ["javascript:alert(1)", {}, null],
    ["data:text/html,<script>", {}, null],
    ["http://evil.example/pay", {}, null],
    ["//evil.example/pay", {}, null],
    ["/relative/path", {}, null],
    ["https://checkout.stripe.com@evil.example/pay", {}, null], // userinfo that reads as a trusted host
    ["http://localhost:3000/return", {}, null], // loopback http only behind the dev flag
    ["http://localhost:3000/return", { allowLoopbackHttp: true }, "http://localhost:3000/return"],
    ["http://[::1]:3000/return", { allowLoopbackHttp: true }, "http://[::1]:3000/return"],
    ["http://evil.example/pay", { allowLoopbackHttp: true }, null],
    ["https://checkout.stripe.com/pay", { allowedOrigins: ["https://checkout.stripe.com"] }, "https://checkout.stripe.com/pay"],
    ["https://evil.example/pay", { allowedOrigins: ["https://checkout.stripe.com"] }, null],
    ["https://checkout.stripe.com/pay", { allowedOrigins: [] }, null], // an empty allowlist admits nothing
    ["http://localhost:3000/r", { allowedOrigins: ["https://checkout.stripe.com"], allowLoopbackHttp: true }, null],
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
