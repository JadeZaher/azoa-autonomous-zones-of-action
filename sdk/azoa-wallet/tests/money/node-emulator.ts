import { randomUUID } from "node:crypto";
import type { CashOutView, FiatCheckoutIntent, FiatCheckoutStatus, FiatSettlementState } from "../../src/api/client.js";

/**
 * A fetch-level fake of the AZOA node's first-party money routes, rebuilt from the node's own code paths
 * (read-only, oasis-sleek): checkout status/settlement from `FiatRailProvider.ProjectAsync` + `ClassifySession`,
 * the same-key resend contract of `FiatRailProvider.BeginCheckoutAsync` (the table in `Providers/Fiat/AGENTS.md`,
 * branch feat/fiat-replay-codes, cee103bd), webhook transitions from `StripeWebhookInbox.ApplyAsync`, cash-out
 * claims from `OffRampService.StartAsync`. Only the process edge (HTTP) is fake.
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
  /** The operator switched the payment rail off: checkout begin answers 503 `PAYMENTS_NOT_CONFIGURED`; existing intents still read. */
  disablePayments(): void;
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

/** One step of the real payment timeline. */
export type CheckoutEvent =
  /** The person paid by card: Stripe's session is complete + paid; no webhook yet (the node still says awaiting_payment). */
  | "pay"
  /** The person finished with an asynchronous method (ACH): complete + unpaid. */
  | "payAsync"
  /** The async method cleared at Stripe. */
  | "asyncCleared"
  /** checkout.session.completed / async_payment_succeeded delivered: the node re-reads Stripe and transitions. */
  | "webhook"
  /** checkout.session.async_payment_failed delivered. */
  | "asyncFailed"
  /** Stripe expired the open page and checkout.session.expired was delivered. */
  | "expire"
  /** The page's expires_at passed and no webhook has arrived yet (an open page expires at Stripe; a complete one stays complete). */
  | "pageLapsed"
  /** The settlement inbox row was applied by the issuance handler (a separate row, later than the webhook). */
  | "applyFunds"
  /** The node's Stripe re-read starts failing: reads and same-key resends answer 503 DEPENDENCY_UNAVAILABLE. */
  | "stripeDown"
  | "stripeUp"
  /** `FiatCreationVerdict.NeedsOperator`: the creation fence cannot decide; an operator must. */
  | "needsOperator"
  /** The operator closed the reviewed intent (nothing was ever payable): it is terminal. */
  | "operatorClosed"
  /** The operator cleared the review and the intent's page can be paid again. */
  | "operatorResumed"
  /** Stripe answers `idempotency_key_in_use`: a same-key creation request is still in flight. */
  | "creationInFlight"
  | "creationSettled";

export interface NodeEmulatorOptions {
  baseUrl?: string;
  /** Milliseconds every answer takes. */
  latencyMs?: number;
  /** Fields merged into every result object, as a node that later adds fields would send them. */
  extraFields?: Record<string, unknown>;
  /** What the person may cash out, in minor units. */
  balanceMinorUnits?: number;
  /** Origin of the payment pages; defaults to Stripe Checkout's (`https://checkout.stripe.com`). A custom domain is another origin. */
  paymentOrigin?: string;
  /** The node lane's first cut (769494f1): a resend after the page's expiry answers INTENT_EXPIRED without the fresh Stripe read. */
  expiryBeforeStripeRead?: boolean;
}

type HostedState = "AwaitingSubject" | "AwaitingOutcome" | "Completed" | "Failed" | "Abandoned";

interface HostedSession {
  admission: Admission;
  state: HostedState;
  stripe: { status: "open" | "complete" | "expired"; paymentStatus: "unpaid" | "paid" };
  stripeReadable: boolean;
  fundsApplied: boolean;
  needsOperator: boolean;
  creationInFlight: boolean;
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
  const paymentOrigin = options.paymentOrigin ?? "https://checkout.stripe.com";
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
  let paymentsDisabled = false;

  const withExtras = (value: unknown): unknown => {
    if (!options.extraFields) return value;
    if (Array.isArray(value)) return value.map(withExtras);
    return value !== null && typeof value === "object" ? { ...value, ...options.extraFields } : value;
  };
  const ok = (result: unknown) => json(200, { isError: false, message: "Success", result: withExtras(result) });
  const fail = (status: number, message: string, code?: string) => json(status, { isError: true, message, ...(code ? { code } : {}) });
  /** §settlement-read-failure: a projection whose Stripe re-read failed is a retryable 503, never a 200 `not_started`. */
  const dependencyUnavailable = () => fail(503, "The payment provider could not be reached. Try again shortly.", "DEPENDENCY_UNAVAILABLE");

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
      return ok({ providerKey: "stripe", flowKind: "checkout", available: !paymentsDisabled, missingConfigurationKeys: [], unavailableReason: null });
    }
    if (path === "/api/kyc/status") {
      return ok({ id: randomUUID(), userId, provider: "MANUAL", providerKey: "manual", status: "APPROVED", hostedVerification: false, submittedAt: now(), createdDate: now() });
    }
    if (path === "/api/kyc/session" && method === "POST") {
      const sessionId = randomUUID();
      return ok({
        provider: "IDENTOMAT", providerKey: "identomat", hostedVerification: true, acceptsDocumentReferences: false, developmentSimulation: false,
        providerSessionId: sessionId, verificationUrl: `https://widget.identomat.com/?session_token=${sessionId}`, expiresAt: null, instructions: null,
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
      if (!session.stripeReadable) return dependencyUnavailable();
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
          setupUrl: method === "POST" ? `https://connect.stripe.com/setup/c/acct_test/${randomUUID()}` : null, setupUrlExpiresAt: null,
          statusMessage: "Your payout account is ready.",
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
    disablePayments() {
      paymentsDisabled = true;
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
    if (paymentsDisabled) return fail(503, "Adding funds is not configured on this node.", "PAYMENTS_NOT_CONFIGURED");
    const existing = sessionsByKey.get(requestKey);
    if (existing) return resend(existing, amountMinorUnits);
    const intentId = randomUUID();
    const admission: Admission = { intentId, requestKey, amountMinorUnits, paymentUrl: `${paymentOrigin}/c/pay/cs_test_${intentId.replace(/-/g, "")}` };
    const session: HostedSession = {
      admission,
      state: "AwaitingSubject",
      stripe: { status: "open", paymentStatus: "unpaid" },
      stripeReadable: true,
      fundsApplied: false,
      needsOperator: false,
      creationInFlight: false,
      expiresAt: Date.now() + 3_600_000,
    };
    sessionsByKey.set(requestKey, session);
    sessionsById.set(intentId, session);
    admissions.push(admission);
    return ok(project(session, false));
  }

  /** The same-key resend contract, in the node's order; only a provably unpaid and closed page answers INTENT_EXPIRED. */
  function resend(session: HostedSession, amountMinorUnits: number): Response {
    if (session.admission.amountMinorUnits !== amountMinorUnits) return fail(409, "That request key is already in use.", "REQUEST_KEY_IN_USE");
    if (session.needsOperator) return fail(409, "This payment needs a manual check before it can continue.", "INTENT_NEEDS_REVIEW");
    if (isTerminal(session.state)) return fail(409, "This payment is already finished.", "INTENT_FINISHED");
    if (session.creationInFlight) return fail(409, "A payment for this request is still being set up.", "CONFLICT");
    const pastExpiry = session.expiresAt <= Date.now();
    if (options.expiryBeforeStripeRead && pastExpiry && session.state === "AwaitingSubject") return expiredPage();
    if (!session.stripeReadable) return dependencyUnavailable();
    // Paid or unpaid, a completed session (or a clearing intent) is resumed with its real projection and no page.
    if (session.stripe.status === "complete" || session.state === "AwaitingOutcome") return ok(project(session, true));
    if (!pastExpiry && session.stripe.status === "open") return ok(project(session, true));
    if (pastExpiry && session.state === "AwaitingSubject") return expiredPage();
    return ok(project(session, true));
  }

  function expiredPage(): Response {
    return fail(409, "This payment page has expired. Start a new payment.", "INTENT_EXPIRED");
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

/** `StripeWebhookInbox.ApplyAsync`, the operator's decisions, and the Stripe-side facts the node re-reads. */
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
      if (session.stripe.status === "open") session.stripe.status = "expired";
      session.expiresAt = Date.now() - 1;
      if (!isTerminal(session.state)) session.state = "Abandoned";
      return;
    case "pageLapsed":
      if (session.stripe.status === "open") session.stripe.status = "expired";
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
    case "needsOperator":
      session.needsOperator = true;
      return;
    case "operatorClosed":
      session.needsOperator = false;
      session.state = "Abandoned";
      return;
    case "operatorResumed":
      session.needsOperator = false;
      return;
    case "creationInFlight":
      session.creationInFlight = true;
      return;
    case "creationSettled":
      session.creationInFlight = false;
      return;
  }
}

/** `FiatRailProvider.ClassifySession`. */
function classify(session: HostedSession): "confirmed" | "failed" | "pending" {
  if (session.stripe.status === "complete") return session.stripe.paymentStatus === "paid" ? "confirmed" : "pending";
  return session.stripe.status === "expired" ? "failed" : "pending";
}

/** `FiatRailProvider.ProjectAsync` (Stripe readable): settlement re-read from Stripe; `funds_applied` only from an applied inbox row. */
function project(session: HostedSession, resumed: boolean): FiatCheckoutIntent {
  let settlementState: FiatSettlementState = "not_started";
  let amountPaidMinorUnits: number | null = null;
  const confirmation = classify(session);
  if (confirmation === "confirmed") {
    amountPaidMinorUnits = session.admission.amountMinorUnits;
    settlementState = session.fundsApplied ? "funds_applied" : "payment_received";
  } else if (confirmation === "failed") {
    settlementState = "payment_failed";
  }
  return {
    intentId: session.admission.intentId,
    // The node's real admission key embeds the subject and tenant ids; the browser view must drop it.
    admissionKey: `${TENANT_ID}:${session.admission.requestKey}`,
    status: STATUS_OF[session.state],
    settlementState,
    // Only an open page inside its lifetime is payable; a completed session or a terminal intent names none.
    paymentUrl: isTerminal(session.state) || session.stripe.status !== "open" ? null : session.admission.paymentUrl,
    resumed,
    amountPaidMinorUnits,
    currency: "usd",
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
