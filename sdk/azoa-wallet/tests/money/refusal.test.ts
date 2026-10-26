import { describe, expect, it } from "vitest";
import { SdkError, SdkErrorCode } from "../../src/core/errors.js";
import { deriveAccountGate, refusalFromSdkError, releasesRequestKey, toBaseUnits } from "../../src/money/index.js";
import type { MoneyRefusalCode } from "../../src/money/index.js";

/** A node failure as `AzoaApiClient` surfaces it. */
function nodeError(status: number | undefined, serverMessage?: string, serverCode?: string): SdkError {
  const code = status === undefined ? SdkErrorCode.NETWORK_ERROR : SdkErrorCode.API_ERROR;
  return new SdkError(code, serverMessage ? `POST /api/fiat/checkout/x: ${serverMessage}` : "POST /api/fiat/checkout/x failed", { status, serverMessage, serverCode });
}

describe("refusalFromSdkError", () => {
  // [node answer, refusal code, may the saved key be forgotten?]
  const cases: [string, SdkError, MoneyRefusalCode, boolean][] = [
    ["consent gate 403", nodeError(403, "No live consent grant covers this tenant-driven signing action; the action was denied.", "FORBIDDEN"), "consent_revoked", true],
    ["F-H8 payout setup 403", nodeError(403, "Payout account setup can only be done by the account holder.", "FORBIDDEN"), "account_holder_required", true],
    ["D-6 KYB via the consent gate", nodeError(403, "KYB_REQUIRED: this tenant must complete business verification first."), "kyb_required", true],
    ["KYC gate", nodeError(403, "KYC_FORBIDDEN: KYC verification required. Complete identity verification to unlock this feature."), "setup_required", true],
    ["AML review", nodeError(400, "Identity verification is complete and an additional compliance check is under review."), "under_review", true],
    ["no payment provider", nodeError(503, "Adding funds is not configured on this node.", "PAYMENTS_NOT_CONFIGURED"), "payments_not_configured", true],
    ["busy rail", nodeError(503, "The payment service is busy. Nothing was charged; try again shortly.", "RAIL_BUSY"), "rail_busy", false],
    ["rate limited", nodeError(429), "rail_busy", false],
    ["typed validation refusal", nodeError(422, "The amount is below the minimum."), "definite", true],
    ["in progress", nodeError(409, "A payment for this request is still being admitted."), "unconfirmed", false],
    ["server fault", nodeError(500), "unconfirmed", false],
    ["lost on the wire", nodeError(undefined), "unconfirmed", false],
    ["expired session", nodeError(401, "Invalid token."), "unauthenticated", false],
    ["client guard before egress", new SdkError(SdkErrorCode.INVALID_INPUT, "intentId must be a UUID"), "definite", true],
  ];

  it.each(cases)("%s", (_label, error, code, releasesKey) => {
    const refusal = refusalFromSdkError(error);
    expect(refusal.code).toBe(code);
    expect(releasesRequestKey(refusal)).toBe(releasesKey);
    expect(refusal.message.trim()).not.toBe("");
  });

  it("keeps the server's sentence but never wire codes, configuration key names or SDK request text", () => {
    expect(refusalFromSdkError(nodeError(403, "KYB_REQUIRED: this tenant must complete business verification first.")).message)
      .toBe("this tenant must complete business verification first.");
    expect(refusalFromSdkError(nodeError(422, "Payments are paused. Missing configuration: Fiat:Stripe:SecretKey")).message).toBe("Payments are paused.");
    expect(refusalFromSdkError(nodeError(500)).message).not.toMatch(/POST|\/api\//);
  });
});

describe("deriveAccountGate", () => {
  const account = { walletId: null, walletAddress: null, identityReady: true, kycReady: true, walletReady: true, unavailableReason: null, reviewDueAt: null };
  it.each([
    ["ready account", { ok: true as const, value: { ...account, kycStatus: "Approved" as const, ready: true } }, "ready"],
    ["compliance review", { ok: true as const, value: { ...account, kycStatus: "UnderReview" as const, ready: false } }, "review"],
    ["unverified", { ok: true as const, value: { ...account, kycStatus: "Unknown" as const, ready: false } }, "setup"],
    ["node cannot onboard", { ok: true as const, value: { ...account, kycStatus: "Unknown" as const, ready: false, kycReady: false } }, "unavailable"],
    ["consent withdrawn", { ok: false as const, refusal: { code: "consent_revoked" as const, message: "x" } }, "revoked"],
    ["no account yet", { ok: false as const, refusal: { code: "setup_required" as const, message: "x" } }, "setup"],
    ["lapsed KYB", { ok: false as const, refusal: { code: "kyb_required" as const, message: "x" } }, "unavailable"],
  ])("%s", (_label, result, gate) => {
    expect(deriveAccountGate(result)).toBe(gate);
  });
});

describe("toBaseUnits", () => {
  it.each([
    ["1.5", 6, "1500000"],
    ["0.000001", 6, "1"],
    ["123456789012345678901234567890", 0, "123456789012345678901234567890"],
    ["1.0000001", 6, null],
    ["1.", 2, null],
    ["-1", 2, null],
    ["1e3", 2, null],
  ])("%s at %i decimals", (text, decimals, expected) => {
    expect(toBaseUnits(text, decimals)).toBe(expected);
  });
});
