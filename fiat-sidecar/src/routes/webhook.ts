import { Router, Request, Response } from "express";
import { randomUUID } from "crypto";
import { orchestrator } from "../azoa";
import { WebhookValidationError } from "@azoa/wallet-sdk/orchestration";

const router = Router();

// HIGH-5 / MEDIUM-6: HTTP mapping is documented in fiat-sidecar/AGENTS.md
// §webhook-mapping. Summary:
//   validation/signature error → 400 (Stripe will NOT retry a bad request)
//   ignored / settled / accepted_pending → 2xx (stop retries; reconcile async)
//   terminal AZOA failure → 500 (Stripe retries with bounded backoff)
router.post("/", async (req: Request, res: Response) => {
  const sig = req.headers["stripe-signature"];
  const correlationId = randomUUID();

  if (typeof sig !== "string") {
    return res.status(400).json({ error: "Missing signature" });
  }

  let outcome;
  try {
    outcome = await orchestrator.handleWebhook(req.body, sig);
  } catch (err) {
    if (err instanceof WebhookValidationError) {
      // Bad signature / missing metadata / bad session id — do not retry.
      console.error(`[webhook ${correlationId}] validation:`, err.message);
      return res.status(400).json({ error: "Invalid webhook", correlationId });
    }
    // Unexpected fault: retryable.
    console.error(`[webhook ${correlationId}] error:`, err);
    return res.status(500).json({ error: "Webhook processing error", correlationId });
  }

  switch (outcome.status) {
    case "settled":
    case "accepted_pending":
    case "ignored":
      return res.json({ received: true, status: outcome.status });
    case "failed_terminal":
      console.error(`[webhook ${correlationId}] terminal:`, outcome.reason);
      return res.status(500).json({ error: "Allocation failed", correlationId });
  }
});

export default router;
