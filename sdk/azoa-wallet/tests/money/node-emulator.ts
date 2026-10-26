import { randomUUID } from "node:crypto";
import type { CashOutView, FiatCheckoutIntent, FiatSettlementState } from "../../src/api/client.js";

/**
 * A fetch-level fake of the AZOA node's first-party money routes (`FiatCheckoutController`, `FiatPayoutController`,
 * `KycController`, `WalletController`). Admission is idempotent on the request key, as the node's admission ledger is,
 * so a test can count what the node actually admitted. Only the process edge (HTTP) is fake.
 */
export interface NodeEmulator {
  baseUrl: string;
  token: string;
  userId: string;
  fetch: typeof fetch;
  /** Every checkout the node admitted (a reused request key never adds one). */
  readonly admissions: readonly FiatCheckoutIntent[];
  /** Every cash-out the node claimed. */
  readonly cashOuts: readonly CashOutView[];
  /** The next checkout start is admitted, then its response is lost on the wire. */
  loseNextAdmissionResponse(): void;
  /** The person's consent for the acting tenant is gone: fiat routes answer the consent gate's 403. */
  revokeConsent(): void;
  /** Each later read of `intentId` advances to the next settlement state. */
  progress(intentId: string, states: FiatSettlementState[]): void;
  /** How many times `intentId` was read. */
  readsOf(intentId: string): number;
}

const TENANT_ID = "7a1d3a52-5a4c-4c3e-9a51-0d6c1f0b9e21";

export function createNodeEmulator(): NodeEmulator {
  const userId = randomUUID();
  const token = `jwt-${userId}`;
  const baseUrl = "https://node.azoa.test";
  const intentsByKey = new Map<string, FiatCheckoutIntent>();
  const admissions: FiatCheckoutIntent[] = [];
  const cashOutsByKey = new Map<string, CashOutView>();
  const cashOuts: CashOutView[] = [];
  const progressions = new Map<string, FiatSettlementState[]>();
  const reads = new Map<string, number>();
  let loseNext = false;
  let consentRevoked = false;

  const ok = (result: unknown) => json(200, { isError: false, message: "Success", result });
  const fail = (status: number, message: string, code?: string) => json(status, { isError: true, message, ...(code ? { code } : {}) });

  const fetchStub = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = new URL(String(input));
    const method = (init?.method ?? "GET").toUpperCase();
    const headers = (init?.headers ?? {}) as Record<string, string>;
    if (headers["Authorization"] !== `Bearer ${token}`) return fail(401, "Invalid token.", "FORBIDDEN");
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
    const path = url.pathname;

    if (path === "/api/fiat/checkout/capabilities") {
      return ok({ providerKey: "stripe", flowKind: "checkout", available: true, missingConfigurationKeys: [], unavailableReason: null });
    }
    if (path === "/api/kyc/status") {
      return ok({ id: randomUUID(), userId, provider: "MANUAL", providerKey: "manual", status: "APPROVED", hostedVerification: false, submittedAt: now(), createdDate: now() });
    }
    if (path === "/api/wallet" && method === "GET") {
      return ok([{ id: randomUUID(), userId, chainType: "algorand", address: "ALGOADDRESS", isDefault: true }]);
    }

    const checkout = /^\/api\/fiat\/checkout\/([^/]+)(?:\/([^/]+))?$/.exec(path);
    if (checkout) {
      if (checkout[1] !== userId) return fail(403, "You can only add funds to your own account.", "FORBIDDEN");
      if (consentRevoked) return consentGateRefusal();
      if (method === "POST") {
        const requestKey = String(body.requestKey);
        const existing = intentsByKey.get(requestKey);
        if (existing) return ok({ ...existing, resumed: true });
        const intent: FiatCheckoutIntent = {
          intentId: randomUUID(),
          // The node's real admission key embeds the subject and tenant ids; the browser view must drop it.
          admissionKey: `${userId}:${TENANT_ID}:${requestKey}`,
          status: "awaiting_payment",
          settlementState: "not_started",
          paymentUrl: `https://checkout.stripe.test/pay/${requestKey}`,
          resumed: false,
          amountPaidMinorUnits: null,
          currency: "usd",
          expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
        };
        intentsByKey.set(requestKey, intent);
        admissions.push(intent);
        if (loseNext) {
          loseNext = false;
          throw new TypeError("fetch failed");
        }
        return ok(intent);
      }
      const intentId = checkout[2] ?? "";
      reads.set(intentId, (reads.get(intentId) ?? 0) + 1);
      const intent = admissions.find(candidate => candidate.intentId === intentId);
      if (!intent) return fail(404, "Not found.", "NOT_FOUND");
      const next = progressions.get(intentId)?.shift();
      if (next) settle(intent, next);
      return ok({ ...intent, resumed: false });
    }

    const payout = /^\/api\/fiat\/payouts\/([^/]+)(?:\/([^/]+))?$/.exec(path);
    if (payout) {
      if (payout[1] !== userId) return fail(403, "You can only cash out from your own account.", "FORBIDDEN");
      if (consentRevoked) return consentGateRefusal();
      if (payout[2] === "availability") {
        return ok({
          available: true, accountReady: true, cashableMinorUnits: 100_00, onHoldMinorUnits: 0, availableFrom: null,
          minimumAmountMinorUnits: 1_00, maximumAmountMinorUnits: 500_00, currencyCode: "usd", statusMessage: "You can cash out now.",
        });
      }
      if (payout[2] === "account") {
        return ok({
          available: true, accountOpened: true, readyToReceive: true, detailsSubmitted: true, outstandingRequirementCount: 0,
          setupUrl: null, setupUrlExpiresAt: null, statusMessage: "Your payout account is ready.",
        });
      }
      if (method === "POST") {
        const requestKey = String(body.requestKey);
        let view = cashOutsByKey.get(requestKey);
        if (!view) {
          view = {
            id: randomUUID(), status: "Requested", statusMessage: "Your cash-out is on its way.", amountMinorUnits: Number(body.amountMinorUnits),
            currencyCode: "usd", requestedAt: now(), paidAt: null, reversedAt: null,
          };
          cashOutsByKey.set(requestKey, view);
          cashOuts.unshift(view);
        }
        return ok(view);
      }
      return ok(cashOuts);
    }

    return fail(404, `No route for ${method} ${path}.`, "NOT_FOUND");
  };

  return {
    baseUrl,
    token,
    userId,
    fetch: fetchStub as typeof fetch,
    get admissions() {
      return admissions;
    },
    get cashOuts() {
      return cashOuts;
    },
    loseNextAdmissionResponse() {
      loseNext = true;
    },
    revokeConsent() {
      consentRevoked = true;
    },
    progress(intentId, states) {
      progressions.set(intentId, [...states]);
    },
    readsOf(intentId) {
      return reads.get(intentId) ?? 0;
    },
  };

  /** The body `TenantConsentGate.Deny` produces, as `FiatCheckoutController` returns it. */
  function consentGateRefusal(): Response {
    return fail(403, "No live consent grant covers this tenant-driven signing action; the action was denied.", "FORBIDDEN");
  }
}

function settle(intent: FiatCheckoutIntent, state: FiatSettlementState): void {
  intent.settlementState = state;
  if (state === "payment_received") {
    intent.status = "processing";
    intent.paymentUrl = null;
  }
  if (state === "funds_applied") {
    intent.status = "complete";
    intent.paymentUrl = null;
    intent.amountPaidMinorUnits = 25_00;
  }
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function now(): string {
  return new Date().toISOString();
}
