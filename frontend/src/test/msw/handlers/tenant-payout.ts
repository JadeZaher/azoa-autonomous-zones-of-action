import { http, HttpResponse, type HttpHandler } from 'msw'
import type { TenantPayoutFloatView, TenantPayoutTopUp } from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { requireBearer } from './auth'

/** A fake of `TenantPayoutBalanceController` (D-8) holding the tenant's floats and top-up intents. */
export interface TenantPayoutApi {
  handlers: HttpHandler[]
  /** Top-up intents keyed by the caller's request key, as the admission key dedupes them. */
  readonly topUps: ReadonlyMap<string, { amountMinorUnits: number; intent: TenantPayoutTopUp }>
  /** Replaces the floats the next GET returns. */
  setFloats(floats: TenantPayoutFloatView[]): void
}

export const TENANT_PAYOUT_SLICE_PAYMENT_URL = 'https://checkout.stripe.test/c/pay/slice-top-up'

/**
 * Follows `Services/Fiat/OffRamp/TenantPayoutBalanceController.cs`: the request key is the admission
 * key, so the same key resumes the same Checkout intent (`resumed: true`) and a different amount under
 * that key is a 409 (`FiatRailProvider` admission mismatch); a blank key is a 400;
 * `paymentsNotConfigured` models a node with no payment provider (503 `PAYMENTS_NOT_CONFIGURED`).
 */
export function tenantPayoutApi(floats: TenantPayoutFloatView[] = [], options: { paymentsNotConfigured?: boolean } = {}): TenantPayoutApi {
  let current = floats
  const topUps = new Map<string, { amountMinorUnits: number; intent: TenantPayoutTopUp }>()
  const handlers = [
    http.get(apiUrl('/api/fiat/tenant-payout-balance'), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk(current))),
    http.post(apiUrl('/api/fiat/tenant-payout-balance/top-up'), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      // No payment provider configured: the controller refuses before any Checkout intent exists.
      if (options.paymentsNotConfigured)
        return HttpResponse.json(azoaError('Adding funds is not configured on this node.', 'PAYMENTS_NOT_CONFIGURED'), { status: 503 })
      const body = (await request.json()) as { requestKey?: string; amountMinorUnits?: number }
      const key = body.requestKey?.trim() ?? ''
      if (!key || key.length > 128)
        return HttpResponse.json(azoaError('A stable request key (1-128 characters) is required.', 'INVALID_REQUEST'), { status: 400 })
      const existing = topUps.get(key)
      if (existing && existing.amountMinorUnits !== body.amountMinorUnits)
        return HttpResponse.json(azoaError('This request key already started a different top-up.', 'CONFLICT'), { status: 409 })
      if (existing) return HttpResponse.json(azoaOk({ ...existing.intent, resumed: true }))
      const intent: TenantPayoutTopUp = {
        intentId: crypto.randomUUID(), admissionKey: `tenant-payout-top-up:${key}`, status: 'awaiting_payment',
        settlementState: 'not_started', paymentUrl: TENANT_PAYOUT_SLICE_PAYMENT_URL, resumed: false,
        amountPaidMinorUnits: null, currency: 'usd', expiresAt: null,
      }
      topUps.set(key, { amountMinorUnits: body.amountMinorUnits ?? 0, intent })
      return HttpResponse.json(azoaOk(intent))
    }),
  ]
  return { handlers, topUps, setFloats: next => { current = next } }
}
