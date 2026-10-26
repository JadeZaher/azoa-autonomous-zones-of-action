// Runtime narrowing of node DTOs into browser-safe views, and the account gate. See `src/money/AGENTS.md` §views.
import type { FiatCheckoutIntent, TenantCustodialAccountStatus } from "../api/client.js";
import type { AccountGateState, CheckoutView, CustodialAccountView, MoneyResult } from "./types.js";

/** Keys that identify the subject or tenant and must never reach a browser. */
export const FORBIDDEN_VIEW_KEYS: readonly string[] = [
  "admissionKey", "tenantId", "externalSubject", "ardanovaUserId", "userId", "subjectUserId",
];

/** Copies only the browser-safe checkout fields (an allowlist, so a new server field cannot leak by default). */
export function toCheckoutView(intent: FiatCheckoutIntent | CheckoutView): CheckoutView {
  return {
    intentId: intent.intentId,
    status: intent.status,
    settlementState: intent.settlementState,
    paymentUrl: intent.paymentUrl ?? null,
    resumed: Boolean(intent.resumed),
    amountPaidMinorUnits: intent.amountPaidMinorUnits ?? null,
    currency: intent.currency ?? null,
    expiresAt: intent.expiresAt ?? null,
  };
}

/** Copies only the browser-safe custodial account fields. */
export function toCustodialAccountView(account: TenantCustodialAccountStatus | CustodialAccountView): CustodialAccountView {
  return {
    walletId: account.walletId ?? null,
    walletAddress: account.walletAddress ?? null,
    kycStatus: account.kycStatus,
    identityReady: account.identityReady,
    kycReady: account.kycReady,
    walletReady: account.walletReady,
    ready: account.ready,
    unavailableReason: account.unavailableReason ?? null,
    reviewDueAt: account.reviewDueAt ?? null,
  };
}

/** Where an account read leaves the person: only `ready` may move value. */
export function deriveAccountGate(result: MoneyResult<CustodialAccountView>): AccountGateState {
  if (!result.ok) {
    switch (result.refusal.code) {
      case "consent_revoked": return "revoked";
      case "setup_required": return "setup";
      case "under_review": return "review";
      default: return "unavailable";
    }
  }
  const account = result.value;
  if (account.ready) return "ready";
  if (account.kycStatus === "UnderReview") return "review";
  if (!account.identityReady || !account.kycReady) return "unavailable";
  return "setup";
}

/** Every forbidden key found anywhere in `value`, as dotted paths; empty means browser-safe. */
export function findForbiddenViewKeys(value: unknown, path = ""): string[] {
  if (value === null || typeof value !== "object") return [];
  if (Array.isArray(value)) return value.flatMap((item, index) => findForbiddenViewKeys(item, `${path}[${index}]`));
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) => {
    const here = path ? `${path}.${key}` : key;
    return [...(FORBIDDEN_VIEW_KEYS.includes(key) ? [here] : []), ...findForbiddenViewKeys(child, here)];
  });
}
