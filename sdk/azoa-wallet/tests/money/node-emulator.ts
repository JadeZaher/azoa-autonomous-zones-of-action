import { randomUUID } from "node:crypto";
import type { CashOutView, FiatCheckoutIntent, FiatCheckoutStatus, FiatSettlementState } from "../../src/api/client.js";

/**
 * A fetch-level fake of the AZOA node's first-party money routes, rebuilt from the node's own code paths
 * (read-only, oasis-sleek branch feat/fiat-replay-codes, re-checked at 33168340): checkout status/settlement from
 * `FiatRailProvider.ProjectAsync` + `ClassifySession`, the same-key resend contract of `BeginCheckoutAsync` +
 * `ResumeAttachedAsync` (the table in `Providers/Fiat/AGENTS.md` §same-key-replay-codes), the request-key void of
 * `FiatRailProvider.Void.cs` (§request-void), the expire-first classifier both share
 * (`FiatRailProvider.SessionVerdict.cs` §stripe-session-verdict: `judgeCheckoutSession` / `settleCheckoutSession`
 * below), webhook transitions from `StripeWebhookInbox.ApplyAsync`, cash-out claims from `OffRampService.StartAsync`.
 * Envelopes are `AZOAResult` as serialized: camelCase, `code` omitted when null, `detail` always present, `result`
 * null unless a final answer names one. Only the process edge (HTTP) is fake.
 */
export interface NodeEmulator {
  baseUrl: string;
  token: string;
  userId: string;
  fetch: typeof fetch;
  /** Every checkout the node admitted (a reused request key never adds one; a void's tombstone is not one). */
  readonly admissions: readonly Admission[];
  /** Every cash-out row the node claimed, newest first (refused ones included, as the ledger keeps them). */
  readonly cashOuts: readonly CashOutView[];
  /** Every request that reached the node. */
  readonly requests: readonly { method: string; path: string; body: Record<string, unknown> }[];
  /** Every answer the node gave, in order (whether or not it reached the caller). */
  readonly answers: readonly { method: string; path: string; status: number; code: string | null }[];
  /** The next POST whose path matches is processed, then its response is lost on the wire; defaults to a checkout start. */
  loseNextResponse(path?: RegExp): void;
  /** The next POST whose path matches waits on the wire, before the node sees it, until the returned release is called. */
  holdNextRequest(path?: RegExp): () => void;
  /** The person's consent for the acting tenant is gone: fiat routes answer the consent gate's 403. */
  revokeConsent(): void;
  /** The operator switched the payment rail off: checkout begin and void answer 503 `PAYMENTS_NOT_CONFIGURED`; existing intents still read. */
  disablePayments(): void;
  /** Applies payment-timeline events to one intent now. */
  apply(intentId: string, ...events: CheckoutEvent[]): void;
  /** Each later read of `intentId` first applies the next group of events. */
  scriptReads(intentId: string, steps: CheckoutEvent[][]): void;
  /** How many times `intentId` was read. */
  readsOf(intentId: string): number;
  /** Whether a void closed `intentId` (its row is ABANDONED with `authoritative_outcome=VOIDED`). */
  isVoided(intentId: string): boolean;
}

export interface Admission {
  intentId: string;
  requestKey: string;
  paymentUrl: string;
  amountMinorUnits: number;
}

/** One step of the real payment timeline. */
export type CheckoutEvent =
  /** The person paid by card: Stripe's session is complete + paid; no webhook yet (the node projects `processing`). */
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
  /** The page's expires_at passed, but Stripe keeps the session `open` because its PaymentIntent is still
   * confirming (a payer mid-3DS, or a slow confirm) -- the exception `pageLapsed` does not model. Only an
   * expire call at Stripe (§stripe-session-verdict), not the clock, can settle a page in this state. */
  | "pageLapsedMidConfirmation"
  /** The settlement inbox row was applied by the issuance handler (a separate row, later than the webhook). */
  | "applyFunds"
  /** The node's Stripe re-read starts failing: reads, same-key resends and voids answer 503 DEPENDENCY_UNAVAILABLE. */
  | "stripeDown"
  | "stripeUp"
  /** Stripe refuses or loses every expire for this page (a Stripe 500 replayed under the expire's idempotency key). */
  | "expireFails"
  /** One-shot: the payer's confirmation lands exactly as the node's own expire call reaches Stripe during a
   * same-key resend or void past the page's expiry, so the expire is refused (the session is no longer
   * `open`) and the fallback fresh read sees it paid instead; `SettleCheckoutSessionAsync` §stripe-session-verdict. */
  | "payDuringExpire"
  /** `FiatCreationVerdict.NeedsOperator`: the creation fence cannot decide; an operator must. */
  | "needsOperator"
  /** The operator closed the reviewed intent (nothing was ever payable): it is terminal. */
  | "operatorClosed"
  /** The operator cleared the review and the intent's page can be paid again. */
  | "operatorResumed"
  /** Stripe answers `idempotency_key_in_use`: a same-key creation request is still in flight (no page attached yet). */
  | "creationInFlight"
  | "creationSettled"
  /** The admission row under this key belongs to another account (`OwnedBy`/`DrivenBy` fail). `BuildAdmissionKey` embeds the subject, so this is unreachable in practice; it exercises the code. */
  | "otherAccount";

export interface NodeEmulatorOptions {
  baseUrl?: string;
  /** Milliseconds every answer takes. */
  latencyMs?: number;
  /** Fields merged into every result object (and a revealed void's `intent`), as a node that later adds fields would send them. */
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
  /** The subject the admission row belongs to. */
  owner: string;
  state: HostedState;
  stripe: { status: "open" | "complete" | "expired"; paymentStatus: "unpaid" | "paid" };
  stripeReadable: boolean;
  expireFails: boolean;
  /** One-shot `payDuringExpire` latch; consumed the next time `settleCheckoutSession` would call Stripe's expire. */
  payerFinishesDuringExpire: boolean;
  fundsApplied: boolean;
  needsOperator: boolean;
  creationInFlight: boolean;
  /** A void closed this row (ABANDONED, VOIDED_AFTER_ADMISSION); no begin may resume it. */
  voided: boolean;
  expiresAt: number;
}

const TENANT_ID = "7a1d3a52-5a4c-4c3e-9a51-0d6c1f0b9e21";
const VOIDED_MESSAGE = "Nothing can be paid under this request key.";
const REVEALED_MESSAGE = "A payment under this request key may already have been made; follow it instead of starting a new one.";

/** `POST /api/fiat/checkout/{userId}`. */
export const CHECKOUT_START = /^\/api\/fiat\/checkout\/[^/]+$/;

/** `POST /api/fiat/checkout/{userId}/requests/{requestKey}/void`. */
export const CHECKOUT_VOID = /^\/api\/fiat\/checkout\/([^/]+)\/requests\/([^/]+)\/void$/;

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
  /** Keys a void claimed before anything was admitted (VOIDED_BEFORE_ADMISSION). */
  const tombstones = new Set<string>();
  const admissions: Admission[] = [];
  const cashOutsByKey = new Map<string, CashOutView>();
  const cashOuts: CashOutView[] = [];
  const scripts = new Map<string, CheckoutEvent[][]>();
  const reads = new Map<string, number>();
  const requests: { method: string; path: string; body: Record<string, unknown> }[] = [];
  const answers: { method: string; path: string; status: number; code: string | null }[] = [];
  let loseNextFor: RegExp | null = null;
  let holdNextFor: { path: RegExp; gate: Promise<void> } | null = null;
  let consentRevoked = false;
  let paymentsDisabled = false;

  const withExtras = (value: unknown): unknown => {
    if (!options.extraFields) return value;
    if (Array.isArray(value)) return value.map(withExtras);
    return value !== null && typeof value === "object" ? { ...value, ...options.extraFields } : value;
  };
  const ok = (result: unknown, message = "Success") => json(200, { isError: false, message, result: withExtras(result), detail: null });
  /** `result` defaults to `null`: only a final same-key 409 (INTENT_FINISHED / INTENT_EXPIRED / INTENT_NEEDS_REVIEW) ever names one. */
  const fail = (status: number, message: string, code?: string, result: unknown = null) =>
    json(status, { isError: true, message, result: withExtras(result), ...(code ? { code } : {}), detail: null });
  /** §settlement-read-failure: a projection whose Stripe re-read failed is a retryable 503, never a 200 `not_started`. */
  const dependencyUnavailable = () => fail(503, "Your payment status could not be confirmed right now. Try again shortly.", "DEPENDENCY_UNAVAILABLE");
  const requestVoided = () => fail(409, "This payment request was cancelled and nothing can be paid under it. Start a new payment.", "REQUEST_VOIDED");
  const keyInUse = () => fail(409, "That request key is already in use.", "REQUEST_KEY_IN_USE");

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

    // `FiatCheckoutController.VoidRequest`, in its order: key checks, rail resolution, then the subject gates Start uses.
    const voidRoute = method === "POST" ? CHECKOUT_VOID.exec(path) : null;
    if (voidRoute) {
      const rawKey = voidRoute[2]!;
      // Routing leaves an encoded '/' as "%2F", which would name a different key than begin stored.
      if (/%2F/i.test(rawKey)) return fail(400, "A request key containing '/' cannot be voided through this route.", "INVALID_REQUEST");
      const requestKey = decodeURIComponent(rawKey);
      if (requestKey.trim() === "") return fail(400, "A request key is required.", "INVALID_REQUEST");
      if (paymentsDisabled) return fail(503, "Adding funds is not configured on this node.", "PAYMENTS_NOT_CONFIGURED");
      if (voidRoute[1] !== userId) return fail(403, "You can only add funds to your own account.", "FORBIDDEN");
      if (consentRevoked) return consentGateRefusal();
      return voidRequest(requestKey);
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
      if (!session || session.owner !== userId) return fail(404, "That payment could not be found.", "NOT_FOUND");
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
    const method = (init?.method ?? "GET").toUpperCase();
    const path = new URL(String(input)).pathname;
    if (method === "POST" && holdNextFor?.path.test(path)) {
      const { gate } = holdNextFor;
      holdNextFor = null;
      await gate;
    }
    if (options.latencyMs) await new Promise(resolve => setTimeout(resolve, options.latencyMs));
    const response = await handle(input, init);
    const envelope = (await response.clone().json()) as { code?: unknown };
    answers.push({ method, path, status: response.status, code: typeof envelope.code === "string" ? envelope.code : null });
    if (method === "POST" && loseNextFor?.test(path)) {
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
    get answers() {
      return answers;
    },
    loseNextResponse(path = CHECKOUT_START) {
      loseNextFor = path;
    },
    holdNextRequest(path = CHECKOUT_START) {
      let release!: () => void;
      holdNextFor = { path, gate: new Promise<void>(resolve => (release = resolve)) };
      return release;
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
    isVoided(intentId) {
      return sessionsById.get(intentId)?.voided === true;
    },
  };

  /** `FiatRailProvider.BeginCheckoutAsync` for a first-party login (the admission key embeds the subject). */
  function beginCheckout(requestKey: string, amountMinorUnits: number): Response {
    if (paymentsDisabled) return fail(503, "Adding funds is not configured on this node.", "PAYMENTS_NOT_CONFIGURED");
    // The claim finds the void's tombstone: a void that won the race against this begin decided the key.
    if (tombstones.has(requestKey)) return requestVoided();
    const existing = sessionsByKey.get(requestKey);
    if (existing) return resend(existing);
    const intentId = randomUUID();
    const admission: Admission = { intentId, requestKey, amountMinorUnits, paymentUrl: `${paymentOrigin}/c/pay/cs_test_${intentId.replace(/-/g, "")}` };
    const session: HostedSession = {
      admission,
      owner: userId,
      state: "AwaitingSubject",
      stripe: { status: "open", paymentStatus: "unpaid" },
      stripeReadable: true,
      expireFails: false,
      payerFinishesDuringExpire: false,
      fundsApplied: false,
      needsOperator: false,
      creationInFlight: false,
      voided: false,
      expiresAt: Date.now() + 3_600_000,
    };
    sessionsByKey.set(requestKey, session);
    sessionsById.set(intentId, session);
    admissions.push(admission);
    return ok(project(session, false));
  }

  /**
   * The same-key resend contract (`BeginCheckoutAsync` + `ResumeAttachedAsync`), in the node's order; only a
   * provably unpaid and closed page answers INTENT_EXPIRED, and the three final codes that can prove anything
   * (INTENT_FINISHED, INTENT_EXPIRED, INTENT_NEEDS_REVIEW) carry the GET projection in `result`.
   */
  function resend(session: HostedSession): Response {
    if (session.owner !== userId) return keyInUse();
    // Before the terminal check, which would otherwise call a voided key "finished".
    if (session.voided) return requestVoided();
    if (session.needsOperator) return needsReviewOnBegin(session);
    if (isTerminal(session.state)) return finishedConflict(session);
    if (session.creationInFlight) return fail(409, "A payment for this request is still being set up.", "CONFLICT");
    const pastExpiry = session.expiresAt <= Date.now();
    // The node lane's first cut (769494f1): INTENT_EXPIRED on the clock alone, bodiless, no Stripe read.
    if (options.expiryBeforeStripeRead && pastExpiry && session.state === "AwaitingSubject") return expiredPage();
    if (!session.stripeReadable) return dependencyUnavailable();
    // Inside its lifetime, or Stripe already moved the claim past "open" (clearing, complete, or resuming):
    // resumed verbatim with its real projection and no page.
    if (!pastExpiry || session.state !== "AwaitingSubject") return ok(project(session, true));
    // Past expiry, still awaiting the payer: the clock proves nothing (Stripe keeps a page `open` for a payer
    // mid-3DS) -- expire it AT STRIPE first, then decide from a fresh read; never assumed. The same classifier
    // the void uses (§stripe-session-verdict), so "voided" and INTENT_EXPIRED cannot disagree again.
    const settled = settleCheckoutSession(session);
    if (settled === "dead") return expiredConflict(session);
    if (settled === "paidOrClearing") return ok(project(session, true));
    return dependencyUnavailable();
  }

  /** `VoidRequestAsync` + `TryVoidAdmittedAsync`: `voided` only on proof; anything paid, clearing or terminal is revealed; Stripe unreadable is 503. */
  function voidRequest(requestKey: string): Response {
    const session = sessionsByKey.get(requestKey);
    if (!session) {
      // The tombstone IS the admission candidate (create-if-absent), so voiding twice gives the same answer.
      tombstones.add(requestKey);
      return voided(null);
    }
    if (session.owner !== userId) return keyInUse();
    if (session.voided) return voided(session.admission.intentId);
    if (isTerminal(session.state) || session.state === "AwaitingOutcome") return revealed(session);
    // An unattached claim: unreplayable → an operator decides; replayable → abandoned before its page could leave the node.
    if (session.needsOperator) return needsReviewOnVoid(session);
    if (session.creationInFlight) return abandon(session);
    if (!session.stripeReadable) return dependencyUnavailable();
    // The same expire-first classifier the same-key resume uses (§stripe-session-verdict).
    const settled = settleCheckoutSession(session);
    if (settled === "dead") return abandon(session);
    if (settled === "paidOrClearing") return revealed(session);
    return fail(503, "Your payment status could not be confirmed right now. Keep this request and try again shortly.", "DEPENDENCY_UNAVAILABLE");
  }

  /** The void's only write: CAS → ABANDONED / VOIDED_AFTER_ADMISSION. */
  function abandon(session: HostedSession): Response {
    session.state = "Abandoned";
    session.voided = true;
    session.creationInFlight = false;
    return voided(session.admission.intentId);
  }

  function voided(intentId: string | null): Response {
    return ok({ outcome: "voided", intentId }, VOIDED_MESSAGE);
  }

  /** `Revealed(ProjectAsync(...))`: the projection GET returns; a projection whose Stripe read fails is the 503 instead. */
  function revealed(session: HostedSession): Response {
    if (!session.stripeReadable) return dependencyUnavailable();
    const intent = project(session, true);
    return ok({ outcome: "revealed", intentId: intent.intentId, intent: withExtras(intent) }, REVEALED_MESSAGE);
  }

  /** `FinalConflict`'s terminal branch: a 409 whose own projection failed to build is the projection's 503, never a bodiless 409. */
  function finishedConflict(session: HostedSession): Response {
    if (!session.stripeReadable) return dependencyUnavailable();
    return fail(409, "This payment is already finished.", "INTENT_FINISHED", project(session, true));
  }

  /** `ResumeAttachedAsync`'s `Dead` branch: the GET projection, settled by `settleCheckoutSession` first. */
  function expiredConflict(session: HostedSession): Response {
    return fail(409, "This payment page has expired. Start a new payment.", "INTENT_EXPIRED", project(session, true));
  }

  /** `NeedsOperator` is always an unattached claim in the real node (`ProjectAsync` is called with `provider: null`), so nothing is ever payable here regardless of the live Stripe fields. */
  function needsReviewProjection(session: HostedSession): FiatCheckoutIntent {
    return { ...project(session, true), paymentUrl: null };
  }

  /** `BeginCheckoutAsync`'s `NeedsOperator` branch: the plain projection, not key-releasing. */
  function needsReviewOnBegin(session: HostedSession): Response {
    return fail(409, "This payment needs a manual check before it can continue.", "INTENT_NEEDS_REVIEW", needsReviewProjection(session));
  }

  /** `NeedsReviewAsync`: the void's 409 wraps the projection in the `revealed` shape, never `voided`. */
  function needsReviewOnVoid(session: HostedSession): Response {
    const intent = needsReviewProjection(session);
    return fail(409, "This payment needs a manual check before it can continue.", "INTENT_NEEDS_REVIEW",
      { outcome: "revealed", intentId: intent.intentId, intent: withExtras(intent) });
  }

  /** The node lane's first cut (769494f1): bodiless, since this predates the 409 `result` feature. */
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
    case "pageLapsedMidConfirmation":
      // Unlike "pageLapsed", Stripe's own status is left untouched: it is still "open" past the clock.
      session.expiresAt = Date.now() - 1;
      return;
    case "payDuringExpire":
      session.payerFinishesDuringExpire = true;
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
    case "expireFails":
      session.expireFails = true;
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
    case "otherAccount":
      session.owner = randomUUID();
      return;
  }
}

/** `FiatRailProvider.ClassifySession`. */
function classify(session: HostedSession): "confirmed" | "failed" | "pending" {
  if (session.stripe.status === "complete") return session.stripe.paymentStatus === "paid" ? "confirmed" : "pending";
  return session.stripe.status === "expired" ? "failed" : "pending";
}

/** `FiatRailProvider.SessionVerdict.cs` `JudgeCheckoutSession`: paid, or Stripe-complete (paid or still clearing), is never dead. */
function judgeCheckoutSession(session: HostedSession): "paidOrClearing" | "dead" | "payable" {
  if (session.stripe.paymentStatus === "paid" || session.stripe.status === "complete") return "paidOrClearing";
  return session.stripe.status === "expired" ? "dead" : "payable";
}

/**
 * `SettleCheckoutSessionAsync`: expires an open page AT STRIPE first (the void's own idempotency key), then
 * decides from a fresh read; never returns "payable". Shared by the void and the same-key resend past expiry
 * (§stripe-session-verdict), so "voided" and `INTENT_EXPIRED` cannot mean different things again.
 */
function settleCheckoutSession(session: HostedSession): "paidOrClearing" | "dead" | "unknown" {
  const verdict = judgeCheckoutSession(session);
  if (verdict !== "payable") return verdict;
  // A payer who finishes exactly as the expire call reaches Stripe gets the expire refused there (the
  // session is no longer `open`); the node's fallback fresh read is what actually proves it paid.
  if (session.payerFinishesDuringExpire) {
    session.payerFinishesDuringExpire = false;
    session.stripe = { status: "complete", paymentStatus: "paid" };
    return "paidOrClearing";
  }
  // Any other refused or lost expire (Stripe busy, a connection reset) is settled by a fresh read, never
  // assumed: `expireFails` leaves `stripe.status` exactly as the payer left it.
  if (!session.expireFails) session.stripe.status = "expired";
  const after = judgeCheckoutSession(session);
  return after === "payable" ? "unknown" : after;
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
  let status = STATUS_OF[session.state];
  // Stripe finished the page before the webhook moved the claim: the payer is done, not waiting.
  if (session.stripe.status === "complete" && status === "awaiting_payment") status = "processing";
  // The page is handed back only while Stripe would still take a payment on it.
  const payable = status === "awaiting_payment" && session.stripe.status === "open";
  return {
    intentId: session.admission.intentId,
    // The node's real admission key embeds the subject and tenant ids; the browser view must drop it.
    admissionKey: `${session.owner}:${TENANT_ID}:${session.admission.requestKey}`,
    status,
    settlementState,
    paymentUrl: payable ? session.admission.paymentUrl : null,
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
