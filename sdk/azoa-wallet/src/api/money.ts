import type { FiatCheckoutIntent } from "./client.js";

/** `POST /api/admin/fiat-payouts/{id}/resolve`: audited exit for a parked or reversed cash-out, honoured only on re-read evidence. */
export type FiatPayoutResolution = "record-paid" | "return-units";
export interface FiatPayoutResolveRequest { resolution: FiatPayoutResolution; reason: string }

/** `POST /api/admin/fiat-reversals/{id}/resolve`: settles an ambiguous move from chain evidence, then re-queues the unwind. */
export interface FiatReversalResolveRequest { reason: string }

/**
 * One tenant payout float (D-8; mirrors `Services/Fiat/OffRamp/TenantPayoutFunding.cs` `TenantPayoutFloatView`).
 * Tenant-priced cash-outs are paid from it, never from the node. `consistent` checks the counters
 * against sums recomputed from the entry rows (`balance = credits − finalized − reserved`).
 */
export interface TenantPayoutFloatView {
  floatId: string;
  tenantId: string;
  currencyCode: string;
  environment: string;
  creditedMinorUnits: number;
  reservedMinorUnits: number;
  finalizedMinorUnits: number;
  availableMinorUnits: number;
  /** Charged-back top-ups not yet covered; while positive, tenant-priced cash-outs are refused. */
  deficitMinorUnits: number;
  /** Settled top-ups still inside the reversal window; they count once they mature. */
  pendingCreditMinorUnits: number;
  recomputedCreditedMinorUnits: number;
  recomputedReservedMinorUnits: number;
  recomputedFinalizedMinorUnits: number;
  consistent: boolean;
  /** Reserved debits older than `TenantPayoutFunding.StrandedReservedAfter` (7d); `consistent` is false while this is nonzero. */
  strandedReservedCount: number;
  /** `requestedAt` of the oldest stranded debit, or null when none is stranded. */
  oldestReservedAt: string | null;
}

/**
 * `POST /api/admin/fiat-payouts/accounts/{userId}/setup/resolve` (RV-L1-01): the operator's exit for a
 * payout-account setup stuck past its Stripe replay window. `attached` means Stripe's own listing named
 * the account that was adopted; `released` means Stripe proved none exists and the claim was freed for a
 * fresh setup (`accountId` is then null).
 */
export interface PayoutAccountSetupResolution {
  outcome: "attached" | "released";
  accountId: string | null;
}

/** `POST /api/fiat/tenant-payout-balance/top-up`: a Checkout payment that credits the float, net of fees, once it matures. */
export interface TenantPayoutTopUpParams {
  requestKey: string;
  amountMinorUnits: number;
  successUrl?: string;
  cancelUrl?: string;
}
export type TenantPayoutTopUp = FiatCheckoutIntent;
