import Stripe from "stripe";
import { AzoaApiClient, AllocationRequest } from "../api/client";
import { isOk } from "../core/result.js";
import { SdkError } from "../core/errors.js";

/**
 * Server-side price schedule: how many AZOA integer base-unit tokens one US cent
 * buys. This is the ONLY source of the mint quantity — it is configured on the
 * orchestrator, never supplied by the client. See `fiat-sidecar/AGENTS.md` §pricing.
 */
export type PriceSchedule =
  | { kind: "tokensPerCent"; tokensPerCent: number };

export interface AzoaStripeOrchestratorConfig {
  stripeSecretKey: string;
  stripeWebhookSecret: string;
  azoaClient: AzoaApiClient;
  successUrl: string;
  cancelUrl: string;
  /**
   * Authoritative tokens-per-cent price schedule. Mint quantity is derived
   * exclusively from the Stripe-confirmed `amount_total` (cents) via this knob.
   */
  priceSchedule: PriceSchedule;
  /** Pinned Stripe API version (MEDIUM-7). Defaults to a known-good version. */
  stripeApiVersion?: Stripe.LatestApiVersion;
}

/**
 * Checkout request. `amount` is the fiat charge in whole/decimal DOLLARS the
 * customer pays — it drives the Stripe line item ONLY. The minted token quantity
 * is derived server-side at webhook time from the Stripe-confirmed cents, never
 * from this value or from client metadata (CRITICAL-1).
 */
export interface CheckoutRequest {
  avatarId: string;
  /** Fiat charge in dollars (decimal string). Priced into Stripe cents; NOT the mint qty. */
  amount: string;
  assetId: string;
  chainType: string;
  kind: "Mint" | "Transfer";
}

/** Discriminated outcome of a webhook so the transport can map HTTP without leaking internals. */
export type WebhookOutcome =
  /** Event was not a completed checkout; nothing to do. Transport → 2xx. */
  | { status: "ignored"; eventType: string }
  /** Allocation confirmed/settled by AZOA. Transport → 2xx. */
  | { status: "settled"; sessionId: string; amountTokens: string; amountTotalCents: number }
  /**
   * AZOA accepted the allocation but it is settling asynchronously (transient
   * in-progress). Stripe MUST stop retrying; backend reconciliation finishes it.
   * Transport → 2xx.
   */
  | { status: "accepted_pending"; sessionId: string; amountTokens: string }
  /**
   * Terminal AZOA failure (not transient). Transport → 5xx so Stripe retries
   * with bounded backoff. `reason` is safe-to-log detail, NOT for the client.
   */
  | { status: "failed_terminal"; sessionId: string; reason: string };

/** Thrown for caller-input / signature / validation faults. Transport → 400 (Stripe won't retry). */
export class WebhookValidationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "WebhookValidationError";
  }
}

export class AzoaStripeOrchestrator {
  private stripe: Stripe;
  private config: AzoaStripeOrchestratorConfig;

  constructor(config: AzoaStripeOrchestratorConfig) {
    this.config = config;
    if (config.priceSchedule.kind === "tokensPerCent") {
      const tpc = config.priceSchedule.tokensPerCent;
      if (!Number.isFinite(tpc) || tpc <= 0) {
        throw new Error("priceSchedule.tokensPerCent must be a positive finite number");
      }
    }
    this.stripe = new Stripe(config.stripeSecretKey, {
      apiVersion: config.stripeApiVersion ?? ("2024-04-10" as Stripe.LatestApiVersion),
    });
  }

  /**
   * Generates a Stripe Checkout session. Metadata carries identity/routing
   * (avatarId/assetId/chainType/kind) ONLY — never the mint quantity, which is
   * recomputed server-side from the confirmed charge at webhook time (CRITICAL-1).
   */
  async createCheckoutSession(request: CheckoutRequest): Promise<string> {
    const parsed = parseFloat(request.amount);
    if (!Number.isFinite(parsed) || parsed <= 0) {
      throw new WebhookValidationError("Checkout amount must be a positive number of dollars");
    }
    const unitAmount = Math.round(parsed * 100);

    const session = await this.stripe.checkout.sessions.create({
      payment_method_types: ["card"],
      line_items: [
        {
          price_data: {
            currency: "usd",
            product_data: { name: `Allocation: ${request.assetId}` },
            unit_amount: unitAmount,
          },
          quantity: 1,
        },
      ],
      mode: "payment",
      success_url: this.config.successUrl,
      cancel_url: this.config.cancelUrl,
      client_reference_id: request.avatarId,
      // Identity/routing only. NO `amount` here — the mint qty is derived from
      // the Stripe-confirmed cents at webhook time.
      metadata: {
        avatarId: request.avatarId,
        assetId: request.assetId,
        chainType: request.chainType,
        kind: request.kind,
      },
    });

    if (!session.url) {
      throw new Error("Failed to create Stripe Checkout session URL");
    }

    return session.url;
  }

  /**
   * Verifies a Stripe webhook signature and, on a completed checkout, makes the
   * idempotent AZOA allocation. Returns a discriminated {@link WebhookOutcome}
   * so the transport maps HTTP correctly. Throws {@link WebhookValidationError}
   * on signature/metadata faults (transport → 400; Stripe must not retry).
   *
   * Mint quantity is derived from the verified event `amount_total` (cents) via
   * the server-side price schedule — never from client metadata (CRITICAL-1).
   * Idempotency-Key is `session.id` (always present, unique) (HIGH-4).
   * See `fiat-sidecar/AGENTS.md` §webhook-mapping for the transient/terminal rules.
   */
  async handleWebhook(rawBody: Buffer | string, signature: string): Promise<WebhookOutcome> {
    let event: Stripe.Event;

    try {
      event = this.stripe.webhooks.constructEvent(
        rawBody,
        signature,
        this.config.stripeWebhookSecret
      );
    } catch (err) {
      // Signature/parse failure is a validation fault: 400, never retried.
      throw new WebhookValidationError(
        `Stripe webhook signature verification failed: ${(err as Error).message}`
      );
    }

    if (event.type !== "checkout.session.completed") {
      return { status: "ignored", eventType: event.type };
    }

    const session = event.data.object as Stripe.Checkout.Session;

    // HIGH-4: session.id is always present and unique per checkout. Guard hard.
    const idempotencyKey = session.id;
    if (typeof idempotencyKey !== "string" || idempotencyKey.length === 0) {
      throw new WebhookValidationError("Stripe session is missing a usable id for idempotency");
    }

    const metadata = session.metadata ?? {};
    const avatarId = metadata.avatarId;
    const assetId = metadata.assetId;
    const chainType = metadata.chainType;
    const kind = metadata.kind as "Mint" | "Transfer" | undefined;

    if (!avatarId || !assetId || !chainType || !kind) {
      throw new WebhookValidationError("Missing AZOA identity/routing metadata in Stripe session");
    }
    if (kind !== "Mint" && kind !== "Transfer") {
      throw new WebhookValidationError(`Unsupported allocation kind in metadata: ${String(kind)}`);
    }

    // CRITICAL-1: authoritative paid amount comes from the verified event.
    // `amount_total` is in cents (Stripe minor units). Read defensively (MEDIUM-7).
    const amountTotalCents = readAmountTotalCents(session);
    if (amountTotalCents === null || amountTotalCents <= 0) {
      throw new WebhookValidationError("Stripe session amount_total is missing or non-positive");
    }

    // Derive the integer base-unit token quantity from cents via the price
    // schedule. This is a DISTINCT field from the fiat cents — the AZOA amount is
    // an integer base-unit string (NumberStyles.None on the backend), never a
    // decimal dollar string.
    const amountTokens = this.computeMintTokens(amountTotalCents);
    if (amountTokens <= 0n) {
      throw new WebhookValidationError(
        "Derived token quantity is zero for the confirmed charge; check the price schedule"
      );
    }
    const amountTokensStr = amountTokens.toString();

    const paymentIntentId =
      typeof session.payment_intent === "string" ? session.payment_intent : idempotencyKey;

    const request: AllocationRequest = {
      kind,
      chainType,
      amount: amountTokensStr, // integer base units — never the dollar/cents value
      assetId,
      name: `Fiat Allocation: ${assetId}`,
      description: `Settled via Stripe checkout ${idempotencyKey} (pi: ${paymentIntentId})`,
    };

    const result = await this.config.azoaClient.allocate(avatarId, request, idempotencyKey);

    if (isOk(result)) {
      return { status: "settled", sessionId: idempotencyKey, amountTokens: amountTokensStr, amountTotalCents };
    }

    // HIGH-5: classify the AZOA failure.
    if (isTransientAllocationError(result.error)) {
      // Backend accepted the claim but the on-chain effect is still settling
      // (InProgress, no TxHash yet). Reconciliation will finish it — tell Stripe
      // we accepted so it STOPS retrying.
      return { status: "accepted_pending", sessionId: idempotencyKey, amountTokens: amountTokensStr };
    }

    return {
      status: "failed_terminal",
      sessionId: idempotencyKey,
      reason: result.error.message,
    };
  }

  /** tokens = floor(cents * tokensPerCent). Integer base units only. */
  private computeMintTokens(amountTotalCents: number): bigint {
    const sched = this.config.priceSchedule;
    if (sched.kind === "tokensPerCent") {
      // Use a floored real multiply; the schedule is operator-controlled so
      // fractional tokensPerCent is allowed but the result is a floored integer.
      const tokens = Math.floor(amountTotalCents * sched.tokensPerCent);
      return BigInt(tokens);
    }
    return 0n;
  }
}

/** Defensive read of the confirmed charge in cents (MEDIUM-7). */
function readAmountTotalCents(session: Stripe.Checkout.Session): number | null {
  const total = session.amount_total;
  if (typeof total === "number" && Number.isFinite(total)) return total;
  return null;
}

/**
 * True when the AZOA allocation error is the RETRYABLE in-progress state
 * (claim won, broadcast started, no TxHash recorded yet) rather than a terminal
 * failure. The backend surfaces this as a 400 AZOAResult whose message contains
 * an "in progress" / "retry" phrase (AllocationManager ~L155-158; ReplayFromRecord
 * in-progress replay). We treat these as accepted-pending so Stripe stops
 * retrying and backend reconciliation settles from chain truth.
 * See `fiat-sidecar/AGENTS.md` §webhook-mapping.
 */
export function isTransientAllocationError(error: SdkError): boolean {
  const msg = error.message.toLowerCase();
  return (
    msg.includes("in progress") ||
    msg.includes("retry once") ||
    msg.includes("already in progress") ||
    msg.includes("has not yet")
  );
}
