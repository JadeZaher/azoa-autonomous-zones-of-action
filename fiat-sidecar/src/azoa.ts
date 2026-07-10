import { AzoaApiClient } from "@azoa/wallet-sdk";
import { AzoaStripeOrchestrator } from "@azoa/wallet-sdk/orchestration";
import { config } from "./config";

export const azoaClient = new AzoaApiClient({
  baseUrl: config.azoaApiUrl,
  apiKey: config.azoaApiKey,
});

export const orchestrator = new AzoaStripeOrchestrator({
  stripeSecretKey: config.stripeSecretKey,
  stripeWebhookSecret: config.stripeWebhookSecret,
  azoaClient,
  successUrl: config.successUrl,
  cancelUrl: config.cancelUrl,
  // CRITICAL-1: server-side price schedule; mint qty derives from confirmed cents.
  priceSchedule: { kind: "tokensPerCent", tokensPerCent: config.tokensPerCent },
});
