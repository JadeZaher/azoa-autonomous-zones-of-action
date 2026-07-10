import dotenv from "dotenv";

dotenv.config();

/** Parse a comma/whitespace-separated env list into a trimmed, non-empty array. */
function parseList(raw: string | undefined): string[] {
  if (!raw) return [];
  return raw
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

function parsePositiveFloat(raw: string | undefined, fallback: number): number {
  const n = Number.parseFloat(raw ?? "");
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

export const config = {
  port: process.env.PORT || 4000,
  stripeSecretKey: process.env.STRIPE_SECRET_KEY || "",
  stripeWebhookSecret: process.env.STRIPE_WEBHOOK_SECRET || "",
  azoaApiUrl: process.env.AZOA_API_URL || "http://localhost:5000",
  azoaApiKey: process.env.AZOA_API_KEY || "",
  successUrl: process.env.SUCCESS_URL || "http://localhost:3000/success",
  cancelUrl: process.env.CANCEL_URL || "http://localhost:3000/cancel",

  // CRITICAL-2: shared secret the checkout route requires as a Bearer token.
  // Requests without it are rejected 401.
  checkoutAuthToken: process.env.CHECKOUT_AUTH_TOKEN || "",

  // CRITICAL-2: CORS allowlist. Empty ⇒ no cross-origin browser access permitted.
  allowedOrigins: parseList(process.env.ALLOWED_ORIGINS),

  // CRITICAL-2: server-side allowlists. Empty ⇒ that dimension is unrestricted
  // (operator opted out); populate in production to lock down routing values.
  allowedAssetIds: parseList(process.env.ALLOWED_ASSET_IDS),
  allowedChainTypes: parseList(process.env.ALLOWED_CHAIN_TYPES),
  allowedKinds: parseList(process.env.ALLOWED_KINDS).length
    ? parseList(process.env.ALLOWED_KINDS)
    : ["Mint", "Transfer"],

  // CRITICAL-1: authoritative price schedule — integer base-unit tokens per US cent.
  // The mint quantity is derived from the Stripe-confirmed cents via this knob,
  // NEVER from client-supplied metadata. See fiat-sidecar/AGENTS.md §pricing.
  tokensPerCent: parsePositiveFloat(process.env.TOKENS_PER_CENT, 1),
};
