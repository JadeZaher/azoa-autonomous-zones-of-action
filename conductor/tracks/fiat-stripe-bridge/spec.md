# Fiat Stripe Bridge (SDK Orchestration)

## The Problem
AZOA is deliberately built to hold no fiat secrets (no Stripe SDK, no Stripe keys). However, node operators still need a way to process fiat payments and map them to AZOA allocations. 

If we absorbed Stripe into AZOA's .NET core, it would violate the core security boundary, expand the PCI blast radius, and require the node operator to have theoretical access to all tenant Stripe keys.

## The Solution
The **Fiat Stripe Orchestrator** in `@azoa/wallet-sdk`. 
Instead of forcing node operators to deploy a standalone microservice, we provide the `AzoaStripeOrchestrator` directly in the TypeScript SDK under the `/orchestration` path. 

This makes the logic consumable across multiple areas—any Node.js backend (e.g. Next.js API Routes, Express, NestJS) can simply import it, inject their keys, and get secure, idempotent fiat bridging out-of-the-box.

## Division of Responsibility

| Component | Responsibility |
| --- | --- |
| **SDK Orchestrator** | Stripe Checkout generation, Webhook signature receipt & verification, Metadata packing |
| **AZOA Engine** | Wallet provisioning, Idempotent asset mint/transfer, KYC gating |

## Architecture

### Usage Example
```typescript
import { AzoaStripeOrchestrator } from "@azoa/wallet-sdk/orchestration";
import { AzoaApiClient } from "@azoa/wallet-sdk/api";

const orchestrator = new AzoaStripeOrchestrator({
  stripeSecretKey: "sk_test_...",
  stripeWebhookSecret: "whsec_...",
  successUrl: "https://my-app.com/success",
  cancelUrl: "https://my-app.com/cancel",
  azoaClient: new AzoaApiClient({ baseUrl: "...", apiKey: "..." })
});

// 1. Generate Checkout
const url = await orchestrator.createCheckoutSession({
  avatarId, amount, assetId, chainType, kind: "Mint"
});

// 2. Handle Webhook
await orchestrator.handleWebhook(req.body, req.headers["stripe-signature"]);
```

### Idempotency (Statelessness)
Because AZOA's allocation endpoint uses bulletproof idempotency, the orchestrator does **not** need its own database to prevent double-mints. If Stripe redelivers a webhook, the orchestrator will blindly call AZOA again. AZOA will recognize the Stripe `PaymentIntent ID` as the `Idempotency-Key`, return a `replayed: true` response, and suppress any duplicate asset creation.

## Reference Implementation
A complete, Docker-ready reference implementation of a standalone sidecar using this SDK orchestrator is provided in `fiat-sidecar/` at the root of the repository.
