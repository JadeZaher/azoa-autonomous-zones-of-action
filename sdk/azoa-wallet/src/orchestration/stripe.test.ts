/**
 * Antagonistic tests for the Fiat Stripe orchestrator hardening.
 *
 * Covers the money-printer + settlement fixes:
 *  1. Invalid webhook signature → rejected, no allocation.
 *  2. Duplicate event (same session.id) → allocate called with the SAME
 *     Idempotency-Key both times (replay-safe single logical mint).
 *  3. Non-`checkout.session.completed` event → no allocation.
 *  4. Mint amount derives from the Stripe-confirmed `amount_total`, NOT metadata
 *     (metadata.amount is huge; amount_total is tiny → tiny mint).
 *  5. Missing/invalid session.id → rejected (no allocation with empty key).
 *
 * Stripe SDK (`constructEvent`) and the AzoaApiClient (`allocate`) are mocked —
 * no network.
 */

import { describe, it, expect, vi, beforeEach } from "vitest";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

// ── Mock the Stripe SDK ────────────────────────────────────────────────────
// Module-level spies the constructed Stripe instance delegates to, so tests can
// drive constructEvent / sessions.create per-case.
const constructEvent = vi.fn();
const sessionsCreate = vi.fn();

vi.mock("stripe", () => {
  class StripeMock {
    webhooks = { constructEvent };
    checkout = { sessions: { create: sessionsCreate } };
    constructor(_key: string, _opts?: unknown) {}
  }
  return { default: StripeMock };
});

// Imported AFTER the mock is registered.
import { AzoaStripeOrchestrator, WebhookValidationError } from "./stripe.js";
import type { AllocationResult } from "../api/client.js";

function makeAllocationResult(): AllocationResult {
  return {
    avatarId: "avatar-1",
    walletId: "wallet-1",
    walletAddress: "ADDR",
    walletProvisioned: false,
    operationId: "op-1",
    replayed: false,
  } as AllocationResult;
}

function makeOrchestrator(allocate = vi.fn().mockResolvedValue(ok(makeAllocationResult()))) {
  const azoaClient = { allocate } as any;
  const orchestrator = new AzoaStripeOrchestrator({
    stripeSecretKey: "sk_test_x",
    stripeWebhookSecret: "whsec_x",
    azoaClient,
    successUrl: "https://app/success",
    cancelUrl: "https://app/cancel",
    priceSchedule: { kind: "tokensPerCent", tokensPerCent: 1 },
  });
  return { orchestrator, allocate };
}

/** A completed-checkout event with controllable amount_total, metadata, id. */
function completedEvent(opts: {
  id?: string;
  amountTotal?: number | null;
  metadata?: Record<string, string>;
  paymentIntent?: string | null;
}) {
  return {
    type: "checkout.session.completed",
    data: {
      object: {
        id: opts.id ?? "cs_test_123",
        amount_total: opts.amountTotal ?? 500,
        payment_intent: opts.paymentIntent ?? "pi_123",
        metadata:
          opts.metadata ?? {
            avatarId: "avatar-1",
            assetId: "asset-1",
            chainType: "algorand",
            kind: "Mint",
          },
      },
    },
  };
}

beforeEach(() => {
  constructEvent.mockReset();
  sessionsCreate.mockReset();
});

describe("CRITICAL — signature verification", () => {
  it("1. rejects an invalid webhook signature and never allocates", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    constructEvent.mockImplementation(() => {
      throw new Error("No signatures found matching the expected signature");
    });

    await expect(orchestrator.handleWebhook("raw", "bad-sig")).rejects.toBeInstanceOf(
      WebhookValidationError
    );
    expect(allocate).not.toHaveBeenCalled();
  });
});

describe("HIGH-4 — idempotency key", () => {
  it("2. duplicate event uses the SAME Idempotency-Key (session.id) both times", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    const event = completedEvent({ id: "cs_stable_999" });
    constructEvent.mockReturnValue(event);

    await orchestrator.handleWebhook("raw", "sig");
    await orchestrator.handleWebhook("raw", "sig"); // Stripe redelivery

    expect(allocate).toHaveBeenCalledTimes(2);
    const key1 = allocate.mock.calls[0][2];
    const key2 = allocate.mock.calls[1][2];
    expect(key1).toBe("cs_stable_999");
    expect(key2).toBe("cs_stable_999");
    // The key is session.id, NOT payment_intent.
    expect(key1).not.toBe("pi_123");
  });

  it("5. rejects a session with a missing/empty id and never allocates", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    constructEvent.mockReturnValue(completedEvent({ id: "" }));

    await expect(orchestrator.handleWebhook("raw", "sig")).rejects.toBeInstanceOf(
      WebhookValidationError
    );
    expect(allocate).not.toHaveBeenCalled();
  });
});

describe("event routing", () => {
  it("3. ignores non-checkout.session.completed events without allocating", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    constructEvent.mockReturnValue({
      type: "payment_intent.created",
      data: { object: {} },
    });

    const outcome = await orchestrator.handleWebhook("raw", "sig");
    expect(outcome.status).toBe("ignored");
    expect(allocate).not.toHaveBeenCalled();
  });
});

describe("CRITICAL-1 — mint amount comes from the confirmed event, not metadata", () => {
  it("4. derives qty from amount_total/price schedule, ignoring a huge metadata.amount", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    // Attacker sets a gigantic metadata.amount but pays only 500 cents.
    constructEvent.mockReturnValue(
      completedEvent({
        amountTotal: 500,
        metadata: {
          avatarId: "avatar-1",
          assetId: "asset-1",
          chainType: "algorand",
          kind: "Mint",
          amount: "999999999999", // hostile — must be ignored
        },
      })
    );

    await orchestrator.handleWebhook("raw", "sig");

    expect(allocate).toHaveBeenCalledTimes(1);
    const request = allocate.mock.calls[0][1];
    // tokensPerCent=1 → 500 cents → 500 tokens. NOT the metadata value.
    expect(request.amount).toBe("500");
    expect(request.amount).not.toBe("999999999999");
  });

  it("4b. applies a fractional tokensPerCent price schedule (floored)", async () => {
    const azoaClient = { allocate: vi.fn().mockResolvedValue(ok(makeAllocationResult())) } as any;
    const orchestrator = new AzoaStripeOrchestrator({
      stripeSecretKey: "sk",
      stripeWebhookSecret: "wh",
      azoaClient,
      successUrl: "s",
      cancelUrl: "c",
      priceSchedule: { kind: "tokensPerCent", tokensPerCent: 0.5 },
    });
    constructEvent.mockReturnValue(completedEvent({ amountTotal: 501 }));

    await orchestrator.handleWebhook("raw", "sig");
    // floor(501 * 0.5) = 250
    expect(azoaClient.allocate.mock.calls[0][1].amount).toBe("250");
  });

  it("4c. rejects a non-positive amount_total (no allocation)", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    constructEvent.mockReturnValue(completedEvent({ amountTotal: 0 }));
    await expect(orchestrator.handleWebhook("raw", "sig")).rejects.toBeInstanceOf(
      WebhookValidationError
    );
    expect(allocate).not.toHaveBeenCalled();
  });

  it("4d. passes an integer base-unit amount string (no decimals)", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    constructEvent.mockReturnValue(completedEvent({ amountTotal: 1234 }));
    await orchestrator.handleWebhook("raw", "sig");
    const amount = allocate.mock.calls[0][1].amount;
    expect(amount).toBe("1234");
    expect(amount).not.toContain(".");
  });
});

describe("HIGH-5 — transient vs terminal classification", () => {
  it("settles when AZOA returns ok", async () => {
    const { orchestrator } = makeOrchestrator();
    constructEvent.mockReturnValue(completedEvent({}));
    const outcome = await orchestrator.handleWebhook("raw", "sig");
    expect(outcome.status).toBe("settled");
  });

  it("returns accepted_pending on a transient in-progress AZOA error", async () => {
    const transient = err(
      new SdkError(
        SdkErrorCode.API_ERROR,
        "POST /api/allocation/x: Allocation broadcast is in progress; the on-chain effect has not yet recorded a transaction hash. Retry once it settles.",
        { status: 400 }
      )
    );
    const { orchestrator } = makeOrchestrator(vi.fn().mockResolvedValue(transient));
    constructEvent.mockReturnValue(completedEvent({}));

    const outcome = await orchestrator.handleWebhook("raw", "sig");
    expect(outcome.status).toBe("accepted_pending");
  });

  it("returns failed_terminal on a genuine AZOA error", async () => {
    const terminal = err(
      new SdkError(SdkErrorCode.API_ERROR, "POST /api/allocation/x: KYC verification required", {
        status: 403,
      })
    );
    const { orchestrator } = makeOrchestrator(vi.fn().mockResolvedValue(terminal));
    constructEvent.mockReturnValue(completedEvent({}));

    const outcome = await orchestrator.handleWebhook("raw", "sig");
    expect(outcome.status).toBe("failed_terminal");
  });
});

describe("metadata guards", () => {
  it("rejects when identity/routing metadata is missing", async () => {
    const { orchestrator, allocate } = makeOrchestrator();
    constructEvent.mockReturnValue(
      completedEvent({ metadata: { assetId: "asset-1", chainType: "algorand", kind: "Mint" } })
    );
    await expect(orchestrator.handleWebhook("raw", "sig")).rejects.toBeInstanceOf(
      WebhookValidationError
    );
    expect(allocate).not.toHaveBeenCalled();
  });
});
