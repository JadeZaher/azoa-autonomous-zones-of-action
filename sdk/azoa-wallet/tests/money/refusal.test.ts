import { describe, expect, it } from "vitest";
import { SdkError, SdkErrorCode } from "../../src/core/errors.js";
import { deriveAccountGate, refusalFromSdkError, releasesRequestKey, toBaseUnits } from "../../src/money/index.js";
import type { MoneyRefusal, MoneyRefusalCode } from "../../src/money/index.js";

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
    ["server fault", nodeError(500), "unconfirmed", false],
    ["lost on the wire", nodeError(undefined), "unconfirmed", false],
    ["expired session", nodeError(401, "Invalid token."), "unauthenticated", false],
    ["client guard before egress", new SdkError(SdkErrorCode.INVALID_INPUT, "intentId must be a UUID"), "definite", true],
    // M1: the node's same-key 409s. Only a typed final answer releases the key (and with a serverId, flows decide by state).
    ["409 without a code (still being admitted)", nodeError(409, "A payment for this request is still being admitted."), "unconfirmed", false],
    ["409 generic CONFLICT (Stripe request still in flight)", nodeError(409, "A payment for this request is still being set up.", "CONFLICT"), "unconfirmed", false],
    ["409 INTENT_FINISHED", nodeError(409, "This payment is already finished.", "INTENT_FINISHED"), "definite", true],
    ["409 INTENT_EXPIRED", nodeError(409, "This payment page has expired. Start a new payment.", "INTENT_EXPIRED"), "definite", true],
    ["409 REQUEST_KEY_IN_USE", nodeError(409, "That request key is already in use.", "REQUEST_KEY_IN_USE"), "definite", true],
    // MEDIUM-3: held for an operator, not "the answer did not arrive"; the key survives.
    ["409 INTENT_NEEDS_REVIEW", nodeError(409, "This payment needs a manual check before it can continue.", "INTENT_NEEDS_REVIEW"), "needs_review", false],
    ["503 DEPENDENCY_UNAVAILABLE (the node's Stripe re-read failed)", nodeError(503, "The payment provider could not be reached.", "DEPENDENCY_UNAVAILABLE"), "unconfirmed", false],
    // L6: status before message text; a code counts only on the status the node sends it with.
    ["503 whose text says KYB_REQUIRED", nodeError(503, "KYB_REQUIRED: upstream verification service unavailable."), "unconfirmed", false],
    ["503 whose text says payments are not configured", nodeError(503, "Payments are not configured upstream (proxy)."), "unconfirmed", false],
    ["503 with a KYB_REQUIRED code", nodeError(503, "Verification could not be checked.", "KYB_REQUIRED"), "unconfirmed", false],
    ["500 with an INTENT_FINISHED code", nodeError(500, "This payment is already finished.", "INTENT_FINISHED"), "unconfirmed", false],
    ["a 403 KYB_REQUIRED code beats consent wording", nodeError(403, "Consent is fine; business verification lapsed.", "KYB_REQUIRED"), "kyb_required", true],
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
    expect(refusalFromSdkError(nodeError(409, "This payment page has expired. Start a new payment.", "INTENT_EXPIRED")).message)
      .toBe("This payment page has expired. Start a new payment.");
  });
});

describe("releasesRequestKey (an allowlist)", () => {
  const refusalWith = (code: unknown) => ({ code, message: "x" }) as unknown as MoneyRefusal;
  it.each([
    ["definite", true], ["setup_required", true], ["under_review", true], ["consent_revoked", true],
    ["account_holder_required", true], ["kyb_required", true], ["payments_not_configured", true],
    ["unconfirmed", false], ["rail_busy", false], ["unauthenticated", false], ["needs_review", false],
    // Host transports deserialize JSON at runtime: anything unknown must keep the key.
    ["Definite", false], ["DEFINITE", false], ["server_error", false], ["timeout", false], ["", false], [" definite", false],
    [undefined, false], [null, false], [403, false],
  ])("%s → %s", (code, releases) => {
    expect(releasesRequestKey(refusalWith(code))).toBe(releases);
  });

  it("keeps the key for a missing refusal", () => {
    expect(releasesRequestKey(undefined)).toBe(false);
    expect(releasesRequestKey(null)).toBe(false);
  });
});

describe("deriveAccountGate", () => {
  const account = { walletId: null, walletAddress: null, identityReady: true, kycReady: true, walletReady: true, unavailableReason: null, reviewDueAt: null };
  it.each([
    ["ready account", { ok: true as const, value: { ...account, kycStatus: "Approved" as const, ready: true } }, "ready"],
    ["compliance review", { ok: true as const, value: { ...account, kycStatus: "UnderReview" as const, ready: false } }, "review"],
    ["unverified", { ok: true as const, value: { ...account, kycStatus: "Unknown" as const, ready: false } }, "setup"],
    ["node cannot onboard", { ok: true as const, value: { ...account, kycStatus: "Unknown" as const, ready: false, kycReady: false } }, "unavailable"],
    ["a truthy non-boolean ready is not ready", { ok: true as const, value: { ...account, kycStatus: "Unknown" as const, ready: "false" as unknown as boolean } }, "setup"],
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
