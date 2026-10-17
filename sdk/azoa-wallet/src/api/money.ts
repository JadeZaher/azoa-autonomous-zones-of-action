import type { FiatCheckoutIntent } from "./client.js";

/** `POST /api/admin/fiat-payouts/{id}/resolve`: audited exit for a parked or reversed cash-out, honoured only on re-read evidence. */
export type FiatPayoutResolution = "record-paid" | "return-units";
export interface FiatPayoutResolveRequest { resolution: FiatPayoutResolution; reason: string }

/** `POST /api/admin/fiat-reversals/{id}/resolve`: settles an ambiguous move from chain evidence, then re-queues the unwind. */
export interface FiatReversalResolveRequest { reason: string }

/**
 * Tenant payout float (D-8): tenant-priced cash-outs are paid from it, never from the node.
 * Coded against the L3 design record (`Services/Fiat/OffRamp/AGENTS.md` §tenant-funded); see `src/api/AGENTS.md` §tenant-payout-float.
 */
export interface TenantPayoutBalance {
  currencyCode: string;
  availableMinorUnits: number;
  reservedMinorUnits: number;
  /** Settled top-ups still inside the reversal window; they count once they age. */
  pendingCreditMinorUnits: number;
}

/** `POST /api/fiat/tenant-payout-balance/top-up`: a Checkout payment that credits the float once settled and aged. */
export interface TenantPayoutTopUpParams {
  requestKey: string;
  amountMinorUnits: number;
  successUrl?: string;
  cancelUrl?: string;
}
export type TenantPayoutTopUp = FiatCheckoutIntent;

/** Operator view `GET /api/admin/tenant-payout-floats/{tenantId}`; `consistent` checks `balance = credits − finalized − reserved`. */
export interface TenantPayoutFloatView extends TenantPayoutBalance {
  tenantId: string;
  creditsMinorUnits: number;
  finalizedMinorUnits: number;
  consistent: boolean;
}
