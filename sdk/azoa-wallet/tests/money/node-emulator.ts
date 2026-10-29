import { randomUUID } from "node:crypto";
import type { CashOutView, FiatCheckoutIntent, FiatCheckoutStatus, FiatSettlementState } from "../../src/api/client.js";

/**
 * A fetch-level fake of the AZOA node's first-party money routes, rebuilt from the node's own code paths
 * (read-only, oasis-sleek branch feat/fiat-replay-codes, re-checked at 33168340): checkout status/settlement from
 * `FiatRailProvider.ProjectAsync` + `ClassifySession` (an unattached claim projects `starting`, with no provider read),
 * the same-key resend contract of `BeginCheckoutAsync` + the creation fence (`FiatHostedCreation`) + `ResumeAttachedAsync`
 * (the table in `Providers/Fiat/AGENTS.md` §same-key-replay-codes), the request-key void of `FiatRailProvider.Void.cs`
 * (§request-void, plus the change the node lane announced: see `unreplayableClaimVoid`), the expire-first classifier
 * both share (`FiatRailProvider.SessionVerdict.cs` §stripe-session-verdict: `judgeCheckoutSession` /
 * `settleCheckoutSession` below), webhook transitions from `StripeWebhookInbox.ApplyAsync`, cash-out claims from
 * `OffRampService.StartAsync`. Envelopes are `AZOAResult` as serialized: camelCase, `code` omitted when null, `detail`
 * always present, `result` null unless a final answer names one. Only the process edge (HTTP) is fake.
 */
export interface NodeEmulator {
  baseUrl: string;
  token: string;
  userId: string;
  fetch: typeof fetch;
  /** Every checkout row the node claimed, attached or not (a reused request key never adds one; a void's tombstone is not one). */
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
  /**
   * The next fresh claim's Stripe create fails, leaving an unattached claim (no page) with its creation fence begun:
   * `busy` is a Stripe 5xx (503 `RAIL_BUSY`), `inFlight` Stripe's 409 `idempotency_key_in_use` (409 `CONFLICT`).
   * Every replay fails the same way until `creationSettled`.
   */
  failNextCreate(how?: "busy" | "inFlight"): void;
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
  /** The page this claim gets once its Stripe create attaches (none is handed out before). */
  paymentUrl: string;
  amountMinorUnits: number;
}

/** One step of the real payment timeline. Page events throw on an unattached claim, claim events on one with a page. */
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
  /** The node's Stripe calls start failing: reads, same-key resends and voids answer 503 DEPENDENCY_UNAVAILABLE; a create, 503 RAIL_BUSY. */
  | "stripeDown"
  | "stripeUp"
  /** Stripe refuses or loses every expire for this page (a Stripe 500 replayed under the expire's idempotency key). */
  | "expireFails"
  /** One-shot: the payer's confirmation lands exactly as the node's own expire call reaches Stripe during a
   * same-key resend or void past the page's expiry, so the expire is refused (the session is no longer
   * `open`) and the fallback fresh read sees it paid instead; `SettleCheckoutSessionAsync` §stripe-session-verdict. */
  | "payDuringExpire"
  /** Unattached claim: an operator quarantined it (`CreationQuarantinedAt`), so it is unreplayable (`FiatCreationVerdict.NeedsOperator`). */
  | "needsOperator"
  /** Unattached claim: the operator closed it (nothing was ever payable): it is terminal. */
  | "operatorClosed"
  /** Unattached claim: the operator reconciled it, attaching the Stripe session its lost create made, so its page can be paid. */
  | "operatorResumed"
  /** Unattached claim: Stripe answers `idempotency_key_in_use` (the claim-keyed create is still in flight there). */
  | "creationInFlight"
  /** Unattached claim: the next replay of its claim-keyed create succeeds and attaches the page. */
  | "creationSettled"
  /** Unattached claim, 4 min+ after its creation fence began: its pinned expiry is inside Stripe's 31-minute floor (§expiry-pin). */
  | "creationLapsed"
  /** Unattached claim, 23 h+ after its creation fence began: past `FiatHostedCreation.ReplayWindow`, so it is unreplayable. */
  | "replayWindowLapsed"
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
  /**
   * How the void answers an unattached claim begin can no longer replay (quarantined, or past the 23 h window).
   * `abandon` (default): the node lane's announced change -- any unattached CLAIMED row is abandoned, 200 `voided`.
   * `needsReview`: 33168340 as released -- 409 `INTENT_NEEDS_REVIEW` with the `revealed` shape. The SDK handles both.
   */
  unreplayableClaimVoid?: "abandon" | "needsReview";
}

type HostedState = "Claimed" | "AwaitingSubject" | "AwaitingOutcome" | "Completed" | "Failed" | "Abandoned";

/** The Stripe Checkout Session the node re-reads, once one is attached to the claim. */
interface StripeSession {
  status: "open" | "complete" | "expired";
  paymentStatus: "unpaid" | "paid";
}

interface HostedSession {
  admission: Admission;
  /** The subject the admission row belongs to. */
  owner: string;
  state: HostedState;
  /** Null for an unattached claim (no `ProviderSessionId` / `RedirectUrl`): nothing payable ever left the node. */
  stripe: StripeSession | null;
  /** How the claim's next Stripe create (a replay under its creation token) answers while it is unattached. */
  create: "ok" | "busy" | "inFlight";
  stripeReadable: boolean;
  expireFails: boolean;
  /** One-shot `payDuringExpire` latch; consumed the next time `settleCheckoutSession` would call Stripe's expire. */
  payerFinishesDuringExpire: boolean;
  fundsApplied: boolean;
  /** `CreationQuarantinedAt` is set: an operator must reconcile this unattached claim. */
  quarantined: boolean;
  /** `CreationStartedAt`, in epoch ms: pins the page's expiry and bounds the replay window. */
  creationStartedAt: number;
  /** A void closed this row (ABANDONED, VOIDED_AFTER_ADMISSION); no begin may resume it. */
  voided: boolean;
  /** Stripe's `expires_at` in epoch ms, stored at attach; null while unattached. */
  expiresAt: number | null;
}

const TENANT_ID = "7a1d3a52-5a4c-4c3e-9a51-0d6c1f0b9e21";
const VOIDED_MESSAGE = "Nothing can be paid under this request key.";
const REVEALED_MESSAGE = "A payment under this request key may already have been made; follow it instead of starting a new one.";
const MINUTE_MS = 60_000;
/** `PinnedCheckoutExpiry`: the configured 30-minute lifetime clamps up to `MinimumPinnedLifetimeMinutes` (35). */
const PINNED_LIFETIME_MS = 35 * MINUTE_MS;
/** `StripeMinimumCheckoutLifetime`: a page that would live less than this is never created. */
const STRIPE_MINIMUM_LIFETIME_MS = 31 * MINUTE_MS;
/** `FiatHostedCreation.ReplayWindow`: Stripe keeps idempotency keys 24 h, so replays stop an hour short. */
const REPLAY_WINDOW_MS = 23 * 60 * MINUTE_MS;

/** `POST /api/fiat/checkout/{userId}`. */
export const CHECKOUT_START = /^\/api\/fiat\/checkout\/[^/]+$/;

/** `POST /api/fiat/checkout/{userId}/requests/{requestKey}/void`. */
export const CHECKOUT_VOID = /^\/api\/fiat\/checkout\/([^/]+)\/requests\/([^/]+)\/void$/;

/** `POST /api/fiat/payouts/{userId}` (not the payout-account setup under it). */
export const CASH_OUT_START = /^\/api\/fiat\/payouts\/[^/]+$/;

/** `FiatIntentStatuses.FromSessionState`. */
const STATUS_OF: Record<HostedState, FiatCheckoutStatus> = {
  Claimed: "starting",
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
  let nextCreateFailure: "busy" | "inFlight" | null = null;
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
  /** `FiatConsumerErrors.RailBusy`: a Stripe 429/5xx. */
  const railBusy = () => fail(503, "The payment service is busy. Nothing was charged; try again shortly.", "RAIL_BUSY");
  /** `FiatConsumerErrors.RequestInFlight`: Stripe's 409, the generic non-final `CONFLICT`. */
  const requestInFlight = () => fail(409, "This payment request is still being processed. Keep this request and try again shortly.", "CONFLICT");
  /** `ProjectAsync`: a claim with a page re-reads Stripe (503 when it cannot); an unattached one projects with no provider read. */
  const projected = (session: HostedSession, resumed = true) => (session.stripe && !session.stripeReadable ? dependencyUnavailable() : ok(project(session, resumed)));

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
      return projected(session);
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
    failNextCreate(how = "busy") {
      nextCreateFailure = how;
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
      state: "Claimed",
      stripe: null,
      create: nextCreateFailure ?? "ok",
      stripeReadable: true,
      expireFails: false,
      payerFinishesDuringExpire: false,
      fundsApplied: false,
      quarantined: false,
      creationStartedAt: Date.now(),
      voided: false,
      expiresAt: null,
    };
    nextCreateFailure = null;
    sessionsByKey.set(requestKey, session);
    sessionsById.set(intentId, session);
    admissions.push(admission);
    return createPage(session);
  }

  /**
   * The same-key resend contract (`BeginCheckoutAsync`, the creation fence, `ResumeAttachedAsync`), in the node's
   * order; only a provably unpaid and closed page, or a claim that never got one, answers INTENT_EXPIRED, and the
   * three final codes that can prove anything (INTENT_FINISHED, INTENT_EXPIRED, INTENT_NEEDS_REVIEW) carry the GET
   * projection in `result`.
   */
  function resend(session: HostedSession): Response {
    if (session.owner !== userId) return keyInUse();
    // Before the terminal check, which would otherwise call a voided key "finished".
    if (session.voided) return requestVoided();
    if (isTerminal(session.state)) return finishedConflict(session);
    const now = Date.now();
    // A live claim that already carries a page is resumed verbatim.
    if (session.stripe && session.expiresAt! > now) return projected(session);
    // The creation fence's verdict: `Attached` re-reads Stripe first, then decides.
    if (session.stripe) return resumeAttached(session, now);
    // `NeedsOperator`: tokenless, quarantined, or begun outside the replay window.
    if (session.state !== "Claimed" || isUnreplayable(session, now)) return needsReviewOnBegin(session);
    // §expiry-pin: a replay keeps the claim's pinned expiry; inside Stripe's floor no page can ever be created.
    if (session.creationStartedAt + PINNED_LIFETIME_MS - now < STRIPE_MINIMUM_LIFETIME_MS) return expiredConflict(session);
    return createPage(session);
  }

  /** `ResumeAttachedAsync`: past expiry and still awaiting the payer, the clock proves nothing (Stripe keeps a page `open` mid-3DS), so it is expired AT STRIPE first. */
  function resumeAttached(session: HostedSession, now: number): Response {
    if (!session.stripeReadable) return dependencyUnavailable();
    // Stripe already moved the claim past "open" (clearing, complete, or resuming): resumed with its real projection and no page.
    if (session.state !== "AwaitingSubject" || session.expiresAt! > now) return ok(project(session, true));
    // The same classifier the void uses (§stripe-session-verdict), so "voided" and INTENT_EXPIRED cannot disagree again.
    const settled = settleCheckoutSession(session);
    if (settled === "dead") return expiredConflict(session);
    if (settled === "paidOrClearing") return ok(project(session, true));
    return dependencyUnavailable();
  }

  /** The claim-keyed Stripe create, then the attach; the create's own answer is the projection (`resumed: false`, a replay too). A failed create leaves the claim unattached. */
  function createPage(session: HostedSession): Response {
    if (session.create === "inFlight") return requestInFlight();
    if (session.create === "busy" || !session.stripeReadable) return railBusy();
    attach(session, session.creationStartedAt + PINNED_LIFETIME_MS);
    return ok(project(session, false));
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
    if (!session.stripe) {
      // No page attached: a URL leaves the node only after the attach CAS, which the abandon takes away.
      if (isUnreplayable(session, Date.now()) && options.unreplayableClaimVoid === "needsReview") return needsReviewOnVoid(session);
      return abandon(session);
    }
    if (!session.stripeReadable) return fail(503, "Your payment status could not be confirmed right now. Keep this request and try again shortly.", "DEPENDENCY_UNAVAILABLE");
    // A page attached: the same expire-first classifier the same-key resume uses (§stripe-session-verdict).
    const settled = settleCheckoutSession(session);
    if (settled === "dead") return abandon(session);
    if (settled === "paidOrClearing") return revealed(session);
    return fail(503, "Your payment status could not be confirmed right now. Keep this request and try again shortly.", "DEPENDENCY_UNAVAILABLE");
  }

  /** The void's only write: CAS → ABANDONED / VOIDED_AFTER_ADMISSION. */
  function abandon(session: HostedSession): Response {
    session.state = "Abandoned";
    session.voided = true;
    return voided(session.admission.intentId);
  }

  function voided(intentId: string | null): Response {
    return ok({ outcome: "voided", intentId }, VOIDED_MESSAGE);
  }

  /** `Revealed(ProjectAsync(...))`: the projection GET returns; a projection whose Stripe read fails is the 503 instead. */
  function revealed(session: HostedSession): Response {
    if (session.stripe && !session.stripeReadable) return dependencyUnavailable();
    const intent = project(session, true);
    return ok({ outcome: "revealed", intentId: intent.intentId, intent: withExtras(intent) }, REVEALED_MESSAGE);
  }

  /** `FinalConflict`'s terminal branch: a 409 whose own projection failed to build is the projection's 503, never a bodiless 409. */
  function finishedConflict(session: HostedSession): Response {
    if (session.stripe && !session.stripeReadable) return dependencyUnavailable();
    return fail(409, "This payment is already finished.", "INTENT_FINISHED", project(session, true));
  }

  /** `ResumeAttachedAsync`'s `Dead` branch (settled first), or a claim whose pinned expiry is inside Stripe's floor (no page ever). */
  function expiredConflict(session: HostedSession): Response {
    return fail(409, "This payment page has expired. Start a new payment.", "INTENT_EXPIRED", project(session, true));
  }

  /** `BeginCheckoutAsync`'s `NeedsOperator` branch: the plain projection (`starting`, no page), not key-releasing. */
  function needsReviewOnBegin(session: HostedSession): Response {
    return fail(409, "This payment needs a manual check before it can continue.", "INTENT_NEEDS_REVIEW", project(session, true));
  }

  /** 33168340's `NeedsReviewAsync`: the void's 409 wraps the projection in the `revealed` shape, never `voided`. */
  function needsReviewOnVoid(session: HostedSession): Response {
    const intent = project(session, true);
    return fail(409, "This payment needs a manual check before it can continue.", "INTENT_NEEDS_REVIEW",
      { outcome: "revealed", intentId: intent.intentId, intent: withExtras(intent) });
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

/** `TryAttachProviderSessionAsync`: the claim now carries an open Stripe page. */
function attach(session: HostedSession, expiresAt: number): void {
  session.stripe = { status: "open", paymentStatus: "unpaid" };
  session.state = "AwaitingSubject";
  session.expiresAt = expiresAt;
}

/** `FiatHostedCreation.IsUnreplayable`: quarantined, or its creation fence began outside the replay window. */
function isUnreplayable(session: HostedSession, now: number): boolean {
  return session.quarantined || session.creationStartedAt <= now - REPLAY_WINDOW_MS;
}

/** The Stripe page an event acts on; a page event on an unattached claim is a test bug (no page ever existed). */
function pageOf(session: HostedSession, event: CheckoutEvent): StripeSession {
  if (!session.stripe) throw new Error(`"${event}" needs a payment page; intent ${session.admission.intentId} is an unattached claim`);
  return session.stripe;
}

/** The claim an event acts on; a claim event on a session that already has a page URL is a test bug. */
function unattached(session: HostedSession, event: CheckoutEvent): HostedSession {
  if (session.stripe) throw new Error(`"${event}" models an unattached claim; intent ${session.admission.intentId} already has a payment page`);
  return session;
}

/** `StripeWebhookInbox.ApplyAsync`, the operator's decisions, and the Stripe-side facts the node re-reads. */
function applyEvent(session: HostedSession, event: CheckoutEvent): void {
  switch (event) {
    case "pay":
      Object.assign(pageOf(session, event), { status: "complete", paymentStatus: "paid" });
      return;
    case "payAsync":
      Object.assign(pageOf(session, event), { status: "complete", paymentStatus: "unpaid" });
      return;
    case "asyncCleared":
      pageOf(session, event).paymentStatus = "paid";
      return;
    case "webhook": {
      const page = pageOf(session, event);
      if (isTerminal(session.state) || !session.stripeReadable) return;
      const confirmation = classify(page);
      if (confirmation === "confirmed") session.state = "Completed";
      else if (confirmation === "failed") session.state = "Failed";
      else session.state = "AwaitingOutcome";
      return;
    }
    case "asyncFailed":
      pageOf(session, event);
      if (!isTerminal(session.state)) session.state = "Failed";
      return;
    case "expire": {
      const page = pageOf(session, event);
      if (page.status === "open") page.status = "expired";
      session.expiresAt = Date.now() - 1;
      if (!isTerminal(session.state)) session.state = "Abandoned";
      return;
    }
    case "pageLapsed": {
      const page = pageOf(session, event);
      if (page.status === "open") page.status = "expired";
      session.expiresAt = Date.now() - 1;
      return;
    }
    case "pageLapsedMidConfirmation":
      // Unlike "pageLapsed", Stripe's own status is left untouched: it is still "open" past the clock.
      pageOf(session, event);
      session.expiresAt = Date.now() - 1;
      return;
    case "payDuringExpire":
      pageOf(session, event);
      session.payerFinishesDuringExpire = true;
      return;
    case "applyFunds":
      pageOf(session, event);
      session.fundsApplied = true;
      return;
    case "stripeDown":
      session.stripeReadable = false;
      return;
    case "stripeUp":
      session.stripeReadable = true;
      return;
    case "expireFails":
      pageOf(session, event);
      session.expireFails = true;
      return;
    case "needsOperator":
      unattached(session, event).quarantined = true;
      return;
    case "operatorClosed":
      unattached(session, event).quarantined = false;
      session.state = "Abandoned";
      return;
    case "operatorResumed":
      unattached(session, event).quarantined = false;
      attach(session, Date.now() + PINNED_LIFETIME_MS);
      return;
    case "creationInFlight":
      unattached(session, event).create = "inFlight";
      return;
    case "creationSettled":
      unattached(session, event).create = "ok";
      return;
    case "creationLapsed":
      unattached(session, event).creationStartedAt = Date.now() - 5 * MINUTE_MS;
      return;
    case "replayWindowLapsed":
      unattached(session, event).creationStartedAt = Date.now() - 24 * 60 * MINUTE_MS;
      return;
    case "otherAccount":
      session.owner = randomUUID();
      return;
  }
}

/** `FiatRailProvider.ClassifySession`. */
function classify(page: StripeSession): "confirmed" | "failed" | "pending" {
  if (page.status === "complete") return page.paymentStatus === "paid" ? "confirmed" : "pending";
  return page.status === "expired" ? "failed" : "pending";
}

/** `FiatRailProvider.SessionVerdict.cs` `JudgeCheckoutSession`: paid, or Stripe-complete (paid or still clearing), is never dead. */
function judgeCheckoutSession(page: StripeSession): "paidOrClearing" | "dead" | "payable" {
  if (page.paymentStatus === "paid" || page.status === "complete") return "paidOrClearing";
  return page.status === "expired" ? "dead" : "payable";
}

/**
 * `SettleCheckoutSessionAsync`: expires an open page AT STRIPE first (the void's own idempotency key), then
 * decides from a fresh read; never returns "payable". Shared by the void and the same-key resend past expiry
 * (§stripe-session-verdict), so "voided" and `INTENT_EXPIRED` cannot mean different things again.
 */
function settleCheckoutSession(session: HostedSession): "paidOrClearing" | "dead" | "unknown" {
  const page = session.stripe!;
  const verdict = judgeCheckoutSession(page);
  if (verdict !== "payable") return verdict;
  // A payer who finishes exactly as the expire call reaches Stripe gets the expire refused there (the
  // session is no longer `open`); the node's fallback fresh read is what actually proves it paid.
  if (session.payerFinishesDuringExpire) {
    session.payerFinishesDuringExpire = false;
    Object.assign(page, { status: "complete", paymentStatus: "paid" });
    return "paidOrClearing";
  }
  // Any other refused or lost expire (Stripe busy, a connection reset) is settled by a fresh read, never
  // assumed: `expireFails` leaves the page's status exactly as the payer left it.
  if (!session.expireFails) page.status = "expired";
  const after = judgeCheckoutSession(page);
  return after === "payable" ? "unknown" : after;
}

/** `FiatRailProvider.ProjectAsync`: settlement re-read from Stripe (none for an unattached claim); `funds_applied` only from an applied inbox row. */
function project(session: HostedSession, resumed: boolean): FiatCheckoutIntent {
  const page = session.stripe;
  let settlementState: FiatSettlementState = "not_started";
  let amountPaidMinorUnits: number | null = null;
  const confirmation = page ? classify(page) : "pending";
  if (confirmation === "confirmed") {
    amountPaidMinorUnits = session.admission.amountMinorUnits;
    settlementState = session.fundsApplied ? "funds_applied" : "payment_received";
  } else if (confirmation === "failed") {
    settlementState = "payment_failed";
  }
  let status = STATUS_OF[session.state];
  // Stripe finished the page before the webhook moved the claim: the payer is done, not waiting.
  if (page?.status === "complete" && (status === "starting" || status === "awaiting_payment")) status = "processing";
  // The page is handed back only while Stripe would still take a payment on it.
  const payable = status === "awaiting_payment" && page?.status === "open";
  return {
    intentId: session.admission.intentId,
    // The node's real admission key embeds the subject and tenant ids; the browser view must drop it.
    admissionKey: `${session.owner}:${TENANT_ID}:${session.admission.requestKey}`,
    status,
    settlementState,
    paymentUrl: payable ? session.admission.paymentUrl : null,
    resumed,
    amountPaidMinorUnits,
    currency: page ? "usd" : null,
    expiresAt: session.expiresAt === null ? null : new Date(session.expiresAt).toISOString(),
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
