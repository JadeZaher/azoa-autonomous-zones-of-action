import { Router, Request, Response } from "express";
import { randomUUID, timingSafeEqual } from "crypto";
import { orchestrator } from "../azoa";
import { config } from "../config";

const router = Router();

/** Constant-time bearer-token check against the configured shared secret. */
function isAuthorized(req: Request): boolean {
  if (!config.checkoutAuthToken) return false; // fail closed if unconfigured
  const header = req.headers["authorization"];
  if (typeof header !== "string" || !header.startsWith("Bearer ")) return false;
  const presented = Buffer.from(header.slice("Bearer ".length));
  const expected = Buffer.from(config.checkoutAuthToken);
  if (presented.length !== expected.length) return false;
  return timingSafeEqual(presented, expected);
}

/** Allowlist gate: empty list ⇒ dimension unrestricted (operator opt-out). */
function allowed(list: string[], value: string): boolean {
  return list.length === 0 || list.includes(value);
}

router.post("/", async (req: Request, res: Response) => {
  // CRITICAL-2: authenticate before trusting any body field.
  if (!isAuthorized(req)) {
    return res.status(401).json({ error: "Unauthorized" });
  }

  const correlationId = randomUUID();
  try {
    const { avatarId, amount, assetId, chainType, kind } = req.body ?? {};

    if (!avatarId || !amount || !assetId || !chainType || !kind) {
      return res.status(400).json({ error: "Missing required fields" });
    }

    // CRITICAL-2: validate routing values against server-side allowlists rather
    // than accepting arbitrary client input.
    if (
      !allowed(config.allowedAssetIds, String(assetId)) ||
      !allowed(config.allowedChainTypes, String(chainType)) ||
      !allowed(config.allowedKinds, String(kind))
    ) {
      return res.status(400).json({ error: "Requested allocation parameters are not permitted" });
    }

    const url = await orchestrator.createCheckoutSession({
      avatarId: String(avatarId),
      amount: String(amount),
      assetId: String(assetId),
      chainType: String(chainType),
      kind,
    });

    res.json({ url });
  } catch (error) {
    // MEDIUM-6: log full detail server-side, return a generic message + id.
    console.error(`[checkout ${correlationId}]`, error);
    res.status(500).json({ error: "Checkout failed", correlationId });
  }
});

export default router;
