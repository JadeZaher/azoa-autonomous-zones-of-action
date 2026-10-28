import { randomUUID } from "node:crypto";
import type { CashOutView, FiatCheckoutIntent, FiatCheckoutStatus, FiatSettlementState } from "../../src/api/client.js";

/**
 * A fetch-level fake of the AZOA node's first-party money routes, rebuilt from the node's own code paths
 * (read-only, oasis-sleek): checkout status/settlement from `FiatRailProvider.ProjectAsync` + `ClassifySession`,
 * same-key admission from `FiatRailProvider.BeginCheckoutAsync`, webhook transitions from `StripeWebhookInbox.ApplyAsync`,
 * cash-out claims from `OffRampService.StartAsync`. Only the process edge (HTTP) is fake.
 */
export interface NodeEmulator {
  baseUrl: string;
  token: string;
  userId: string;
  fetch: typeof fetch;
  /** Every checkout the node admitted (a reused request key never adds one). */
  readonly admissions: readonly Admission[];
  /** Every cash-out row the node claimed, newest first (refused ones included, as the ledger keeps them). */
  readonly cashOuts: readonly CashOutView[];
  /** Every request that reached the node. */
  readonly requests: readonly { method: string; path: string; body: Record<string, unknown> }[];
  /** The next POST whose path matches is processed, then its response is lost on the wire; defaults to a checkout start. */
  loseNextResponse(path?: RegExp): void;
  /** The person's consent for the acting tenant is gone: fiat routes answer the consent gate's 403. */
  revokeConsent(): void;
  /** Applies payment-timeline events to one intent now. */
  apply(intentId: string, ...events: CheckoutEvent[]): void;
  /** Each later read of `intentId` first applies the next group of events. */
  scriptReads(intentId: string, steps: CheckoutEvent[][]): void;
  /** How many times `intentId` was read. */
  readsOf(intentId: string): number;
}

export interface Admission {
  intentId: string;
  requestKey: string;
  paymentUrl: string;
  amountMinorUnits: number;
}

/**
 * `legacy`: today's node, every same-key 409 carries `CONFLICT`. `typed`: the parallel node lane's machine codes
 * (`INTENT_FINISHED`, `INTENT_EXPIRED`, `REQUEST_KEY_IN_USE`, `INTENT_NEEDS_REVIEW`).
 */
export type ConflictCodes = "legacy" | "typed";

/** One step of the real payment timeline. */
export type CheckoutEvent =
  /** The person paid by card: Stripe's session is complete + paid; no webhook yet (the node still says awaiting_payment). */
  | "pay"
  /** The person finished with an asynchronous method: complete + unpaid. */
  | "payAsync"
  /** The async method cleared at Stripe. */
  | "asyncCleared"
  /** checkout.session.completed / async_payment_succeeded delivered: the node re-reads Stripe and transitions. */
  | "webhook"
  /** checkout.session.async_payment_failed delivered. */
  | "asyncFailed"
  /** Stripe expired the page and checkout.session.expired was delivered. */
  | "expire"
  /** The page's expires_at passed and no webhook has arrived yet. */
  | "pageLapsed"
  /** The settlement inbox row was applied by the issuance handler (a separate row, later than the webhook). */
  | "applyFunds"
  /** The node's Stripe re-read starts failing (it then reports not_started). */
  | "stripeDown"
  | "stripeUp";

export interface NodeEmulatorOptions {
  baseUrl?: string;
  conflictCodes?: ConflictCodes;
  /** Milliseconds every answer takes. */
  latencyMs?: number;
  /** Fields merged into every result object, as a node that later adds fields would send them. */
  extraFields?: Record<string, unknown>;
  /** What the person may cash out, in minor units. */
  balanceMinorUnits?: number;
}

type HostedState = "AwaitingSubject" | "AwaitingOutcome" | "Completed" | "Failed" | "Abandoned";

interface HostedSession {
  admission: Admission;
  state: HostedState;
  stripe: { status: "open" | "complete" | "expired"; paymentStatus: "unpaid" | "paid" };
  stripeReadable: boolean;
  fundsApplied: boolean;
  expiresAt: number;
}

const TENANT_ID = "7a1d3a52-5a4c-4c3e-9a51-0d6c1f0b9e21";

/** `POST /api/fiat/checkout/{userId}`. */
export const CHECKOUT_START = /^\/api\/fiat\/checkout\/[^/]+$/;

/** `POST /api/fiat/payouts/{userId}` (not the payout-account setup under it). */
export const CASH_OUT_START = /^\/api\/fiat\/payouts\/[^/]+$/;

/** `FiatIntentStatuses.FromSessionState`. */
const STATUS_OF: Record<HostedState, FiatCheckoutStatus> = {
  AwaitingSubject: "awaiting_payment",
  AwaitingOutcome: "processing",
  Completed: "complete",
  Failed: "failed",
  Abandoned: "expired",
};

const isTerminal = (state: HostedState) => state === "Completed" || state === "Failed" || state === "Abandoned";

export function createNodeEmulator(options: NodeEmulatorOptions = {}): NodeEmulator {
  const userId = randomUUID();
  const token = `jwt-${userId}`;
  const baseUrl = options.baseUrl ?? "https://node.azoa.test";
  const typed = options.conflictCodes === "typed";
  const sessionsByKey = new Map<string, HostedSession>();
  const sessionsById = new Map<string, HostedSession>();
  const admissions: Admission[] = [];
  const cashOutsByKey = new Map<string, CashOutView>();
  const cashOuts: CashOutView[] = [];
  const scripts = new Map<string, CheckoutEvent[][]>();
  const reads = new Map<string, number>();
  const requests: { method: string; path: string; body: Record<string, unknown> }[] = [];
  let loseNextFor: RegExp | null = null;
  let consentRevoked = false;

  const withExtras = (value: unknown): unknown => {
    if (!options.extraFields) return value;
    if (Array.isArray(value)) return value.map(withExtras);
    return value !== null && typeof value === "object" ? { ...value, ...options.extraFields } : value;
  };
  const ok = (result: unknown) => json(200, { isError: false, message: "Success", result: withExtras(result) });
  const fail = (status: number, message: string, code?: string) => json(status, { isError: true, message, ...(code ? { code } : {}) });
  const conflict = (message: string, typedCode: string) => fail(409, message, typed ? typedCode : "CONFLICT");

  const apply = (intentId: string, ...events: CheckoutEvent[]) => {
    const session = sessionsById.get(intentId);
    if (!session) throw new Error(`no intent ${intentId}`);
    for (const event of events) applyEvent(session, event);
  };

  const handle = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = new URL(String(input));
    const method = (init?.method ?? "GET").toUpperCase();
    const headers = (init?.headers ?? {}) as Record<string, string>;
    const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
    const path = url.pathname;
    requests.push({ method, path, body });
    if (headers["Authorization"] !== `Bearer ${token}`) return fail(401, "Invalid token.", "FORBIDDEN");

    if (path === "/api/fiat/checkout/capabilities") {
      return ok({ providerKey: "stripe", flowKind: "checkout", available: true, missingConfigurationKeys: [], unavailableReason: null });
    }
    if (path === "/api/kyc/status") {
      return ok({ id: randomUUID(), userId, provider: "MANUAL", providerKey: "manual", status: "APPROVED", hostedVerification: false, submittedAt: now(), createdDate: now() });
    }
    if (path === "/api/kyc/session" && method === "POST") {
      return ok({
        provider: "GENERIC_HOSTED", providerKey: "hosted", hostedVerification: true, acceptsDocumentReferences: false, developmentSimulation: false,
        providerSessionId: `vs_${randomUUID()}`, verificationUrl: "https://verify.azoa.test/session/1", expiresAt: null, instructions: null,
      });
    }
    if (path === "/api/wallet" && method === "GET") {
      return ok([{ id: randomUUID(), userId, chainType: "algorand", address: "ALGOADDRESS", isDefault: true }]);
    }

    const checkout = /^\/api\/fiat\/checkout\/([^/]+)(?:\/([^/]+))?$/.exec(path);
    if (checkout) {
      if (checkout[1] !== userId) return fail(403, "You can only add funds to your own account.", "FORBIDDEN");
      if (consentRevoked) return consentGateRefusal();
      if (method === "POST") return beginCheckout(String(body.requestKey), Number(body.amountMinorUnits));
      const intentId = checkout[2] ?? "";
      reads.set(intentId, (reads.get(intentId) ?? 0) + 1);
      const session = sessionsById.get(intentId);
      // GetIntentAsync: one refusal for "no such intent" and "somebody else's".
      if (!session) return fail(404, "That payment could not be found.", "NOT_FOUND");
      for (const event of scripts.get(intentId)?.shift() ?? []) applyEvent(session, event);
      return ok(project(session, true));
    }

    const payout = /^\/api\/fiat\/payouts\/([^/]+)(?:\/([^/]+))?$/.exec(path);
    if (payout) {
      if (payout[1] !== userId) return fail(403, "You can only cash out from your own account.", "FORBIDDEN");
      if (consentRevoked) return consentGateRefusal();
      if (payout[2] === "availability") {
        return ok({
          available: true, accountReady: true, cashableMinorUnits: cashable(), onHoldMinorUnits: 0, availableFrom: null,
          minimumAmountMinorUnits: 1_00, maximumAmountMinorUnits: 500_00, currencyCode: "USD", statusMessage: "You can cash out now.",
        });
      }
      if (payout[2] === "account") {
        return ok({
          available: true, accountOpened: true, readyToReceive: true, detailsSubmitted: true, outstandingRequirementCount: 0,
          setupUrl: method === "POST" ? "https://connect.stripe.test/setup/1" : null, setupUrlExpiresAt: null, statusMessage: "Your payout account is ready.",
        });
      }
      if (method === "POST") return startCashOut(String(body.requestKey), Number(body.amountMinorUnits));
      return ok(cashOuts);
    }

    return fail(404, `No route for ${method} ${path}.`, "NOT_FOUND");
  };

  const fetchStub = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    if (options.latencyMs) await new Promise(resolve => setTimeout(resolve, options.latencyMs));
    const response = await handle(input, init);
    const path = new URL(String(input)).pathname;
    if ((init?.method ?? "GET").toUpperCase() === "POST" && loseNextFor?.test(path)) {
      loseNextFor = null;
      throw new TypeError("fetch failed");
    }
    return response;
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
    get requests() {
      return requests;
    },
    loseNextResponse(path = CHECKOUT_START) {
      loseNextFor = path;
    },
    revokeConsent() {
      consentRevoked = true;
    },
    apply,
    scriptReads(intentId, steps) {
      scripts.set(intentId, steps.map(step => [...step]));
    },
    readsOf(intentId) {
      return reads.get(intentId) ?? 0;
    },
  };

  /** `FiatRailProvider.BeginCheckoutAsync` for a first-party login (the admission key embeds the subject). */
  function beginCheckout(requestKey: string, amountMinorUnits: number): Response {
    const existing = sessionsByKey.get(requestKey);
    if (existing) {
      if (isTerminal(existing.state)) return conflict("This payment is already finished.", "INTENT_FINISHED");
      if (existing.expiresAt > Date.now()) return ok(project(existing, true));
      // The claim's page is attached but expired: the creation fence answers Attached.
      return conflict("This payment page has expired. Start a new payment.", "INTENT_EXPIRED");
    }
    const intentId = randomUUID();
    const admission: Admission = { intentId, requestKey, amountMinorUnits, paymentUrl: `https://checkout.stripe.test/pay/${intentId}` };
    const session: HostedSession = {
      admission,
      state: "AwaitingSubject",
      stripe: { status: "open", paymentStatus: "unpaid" },
      stripeReadable: true,
      fundsApplied: false,
      expiresAt: Date.now() + 3_600_000,
    };
    sessionsByKey.set(requestKey, session);
    sessionsById.set(intentId, session);
    admissions.push(admission);
    return ok(project(session, false));
  }

  /** `OffRampService.StartAsync`: claim first, then refuse durably; a same-key replay returns the row verbatim, refused or not. */
  function startCashOut(requestKey: string, amountMinorUnits: number): Response {
    const existing = cashOutsByKey.get(requestKey);
    if (existing) return ok(existing);
    const view: CashOutView = {
      id: randomUUID(), status: "Requested", statusMessage: "Getting your money ready to send.", amountMinorUnits,
      currencyCode: "USD", requestedAt: now(), paidAt: null, reversedAt: null,
    };
    const available = cashable();
    cashOutsByKey.set(requestKey, view);
    cashOuts.unshift(view);
    if (amountMinorUnits > available) {
      view.status = "Refused";
      view.statusMessage = "That is more than you have available to cash out.";
      return fail(400, view.statusMessage, "INVALID_REQUEST");
    }
    return ok(view);
  }

  function cashable(): number {
    const committed = cashOuts.filter(row => row.status !== "Refused" && row.status !== "Returned").reduce((sum, row) => sum + row.amountMinorUnits, 0);
    return (options.balanceMinorUnits ?? 100_00) - committed;
  }

  /** The body `TenantConsentGate.Deny` produces, as `FiatCheckoutController` returns it. */
  function consentGateRefusal(): Response {
    return fail(403, "No live consent grant covers this tenant-driven signing action; the action was denied.", "FORBIDDEN");
  }
}

/** `StripeWebhookInbox.ApplyAsync` and the Stripe-side facts the node re-reads. */
function applyEvent(session: HostedSession, event: CheckoutEvent): void {
  switch (event) {
    case "pay":
      session.stripe = { status: "complete", paymentStatus: "paid" };
      return;
    case "payAsync":
      session.stripe = { status: "complete", paymentStatus: "unpaid" };
      return;
    case "asyncCleared":
      session.stripe.paymentStatus = "paid";
      return;
    case "webhook": {
      if (isTerminal(session.state) || !session.stripeReadable) return;
      const confirmation = classify(session);
      if (confirmation === "confirmed") session.state = "Completed";
      else if (confirmation === "failed") session.state = "Failed";
      else session.state = "AwaitingOutcome";
      return;
    }
    case "asyncFailed":
      if (!isTerminal(session.state)) session.state = "Failed";
      return;
    case "expire":
      session.stripe.status = "expired";
      session.expiresAt = Date.now() - 1;
      if (!isTerminal(session.state)) session.state = "Abandoned";
      return;
    case "pageLapsed":
      session.stripe.status = "expired";
      session.expiresAt = Date.now() - 1;
      return;
    case "applyFunds":
      session.fundsApplied = true;
      return;
    case "stripeDown":
      session.stripeReadable = false;
      return;
    case "stripeUp":
      session.stripeReadable = true;
      return;
  }
}

/** `FiatRailProvider.ClassifySession`. */
function classify(session: HostedSession): "confirmed" | "failed" | "pending" {
  if (session.stripe.status === "complete") return session.stripe.paymentStatus === "paid" ? "confirmed" : "pending";
  return session.stripe.status === "expired" ? "failed" : "pending";
}

/** `FiatRailProvider.ProjectAsync`: settlement re-read from Stripe; `funds_applied` only from an applied inbox row. */
function project(session: HostedSession, resumed: boolean): FiatCheckoutIntent {
  let settlementState: FiatSettlementState = "not_started";
  let amountPaidMinorUnits: number | null = null;
  let currency: string | null = null;
  if (session.stripeReadable) {
    currency = "usd";
    const confirmation = classify(session);
    if (confirmation === "confirmed") {
      amountPaidMinorUnits = session.admission.amountMinorUnits;
      settlementState = "payment_received";
    } else if (confirmation === "failed") {
      settlementState = "payment_failed";
    }
  }
  if (settlementState === "payment_received" && session.fundsApplied) settlementState = "funds_applied";
  return {
    intentId: session.admission.intentId,
    // The node's real admission key embeds the subject and tenant ids; the browser view must drop it.
    admissionKey: `${TENANT_ID}:${session.admission.requestKey}`,
    status: STATUS_OF[session.state],
    settlementState,
    paymentUrl: isTerminal(session.state) ? null : session.admission.paymentUrl,
    resumed,
    amountPaidMinorUnits,
    currency,
    expiresAt: new Date(session.expiresAt).toISOString(),
  };
}

/** Routes one global fetch to several emulated nodes by origin. */
export function routeFetch(...nodes: NodeEmulator[]): typeof fetch {
  return (async (input: RequestInfo | URL, init?: RequestInit) => {
    const origin = new URL(String(input)).origin;
    const node = nodes.find(candidate => new URL(candidate.baseUrl).origin === origin);
    if (!node) throw new TypeError(`no emulated node at ${origin}`);
    return node.fetch(input, init);
  }) as typeof fetch;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function now(): string {
  return new Date().toISOString();
}
