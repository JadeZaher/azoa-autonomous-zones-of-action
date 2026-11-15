import { http, HttpResponse, type HttpHandler } from 'msw'
import type { CashOutQuote, CashOutView, FiatCheckoutIntent, HostedSessionCapabilities, PayoutAccountStatus } from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { requireBearer } from './auth'

/** A fake of `Controllers/FiatPayoutController.cs`: availability, payout account, keyed start, history. */
export interface CashOutApi {
  handlers: HttpHandler[]
  /** Every cash-out the node has claimed, newest first; a reused request key never adds one. */
  readonly claims: readonly CashOutView[]
  readonly attemptedRequestKeys: readonly string[]
  readonly voidCalls: readonly string[]
  /** The next start is admitted, then its response is lost (network failure). */
  loseNextResponse(): void
  /** The next start is refused with this status before anything is claimed. */
  refuseNext(status: number, message: string): void
}

/** Same request key → the same cash-out (the service's admission ledger); history lists claims newest first. */
export function cashOutApi(): CashOutApi {
  const byKey = new Map<string, CashOutView>()
  const claims: CashOutView[] = []
  const attemptedRequestKeys: string[] = []
  const voidCalls: string[] = []
  const voided = new Set<string>()
  let lose = false
  let refusal: { status: number; message: string } | null = null
  const quote: CashOutQuote = {
    available: true, accountReady: true, cashableMinorUnits: 100_00, onHoldMinorUnits: 0, availableFrom: null,
    minimumAmountMinorUnits: 1_00, maximumAmountMinorUnits: 500_00, currencyCode: 'usd', statusMessage: 'You can cash out now.',
  }
  const account: PayoutAccountStatus = {
    available: true, accountOpened: true, readyToReceive: true, detailsSubmitted: true, outstandingRequirementCount: 0,
    setupUrl: null, setupUrlExpiresAt: null, statusMessage: 'Your payout account is ready.',
  }
  const base = '/api/fiat/payouts/:userId'

  const handlers = [
    http.get(apiUrl(`${base}/availability`), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk(quote))),
    http.get(apiUrl(`${base}/account`), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk(account))),
    http.get(apiUrl(base), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk(claims))),
    http.post(apiUrl(`${base}/requests/:requestKey/void`), ({ request, params }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const key = String(params.requestKey)
      voidCalls.push(key)
      const existing = byKey.get(key)
      if (existing) return HttpResponse.json(azoaOk({ outcome: 'revealed', cashOut: existing }))
      voided.add(key)
      return HttpResponse.json(azoaOk({ outcome: 'voided' }))
    }),
    http.post(apiUrl(base), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const body = (await request.json()) as { requestKey: string; amountMinorUnits: number }
      attemptedRequestKeys.push(body.requestKey)
      if (voided.has(body.requestKey)) return HttpResponse.json(azoaError('This request was voided.', 'REQUEST_VOIDED'), { status: 409 })
      if (refusal) {
        const { status, message } = refusal
        refusal = null
        return HttpResponse.json(azoaError(message), { status })
      }
      let view = byKey.get(body.requestKey)
      if (!view) {
        view = {
          id: crypto.randomUUID(), status: 'Requested', statusMessage: 'Your cash-out is on its way.', amountMinorUnits: body.amountMinorUnits,
          currencyCode: 'usd', requestedAt: new Date().toISOString(), paidAt: null, reversedAt: null,
        }
        byKey.set(body.requestKey, view)
        claims.unshift(view)
      }
      if (lose) {
        lose = false
        return HttpResponse.error()
      }
      return HttpResponse.json(azoaOk(view))
    }),
  ]

  return {
    handlers,
    get claims() { return claims },
    get attemptedRequestKeys() { return attemptedRequestKeys },
    get voidCalls() { return voidCalls },
    loseNextResponse() { lose = true },
    refuseNext(status, message) { refusal = { status, message } },
  }
}

/** A fake of `Controllers/FiatCheckoutController.cs`: capabilities, keyed start (resume on reuse), read by intent id. */
export interface CheckoutApi {
  handlers: HttpHandler[]
  /** Intents created, oldest first; a reused request key resumes instead of adding one. */
  readonly intents: readonly FiatCheckoutIntent[]
  /** Return URLs the client asked for on each start. */
  readonly successUrls: readonly (string | undefined)[]
  /** The provider confirmed payment for this intent and the funds were applied. */
  settle(intentId: string): void
}

export function checkoutApi(): CheckoutApi {
  const byKey = new Map<string, string>()
  const intents: FiatCheckoutIntent[] = []
  const successUrls: (string | undefined)[] = []
  const capabilities: HostedSessionCapabilities = {
    providerKey: 'stripe', flowKind: 'checkout', available: true, missingConfigurationKeys: [], unavailableReason: null,
  }
  const find = (intentId: string) => intents.find(intent => intent.intentId === intentId)

  const handlers = [
    http.get(apiUrl('/api/fiat/checkout/capabilities'), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk(capabilities))),
    http.get(apiUrl('/api/fiat/checkout/:userId/:intentId'), ({ request, params }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const intent = find(String(params.intentId))
      return intent ? HttpResponse.json(azoaOk({ ...intent, resumed: false })) : HttpResponse.json(azoaError('Not found.', 'NOT_FOUND'), { status: 404 })
    }),
    http.post(apiUrl('/api/fiat/checkout/:userId'), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const body = (await request.json()) as { requestKey: string; amountMinorUnits: number; successUrl?: string }
      successUrls.push(body.successUrl)
      const existing = byKey.get(body.requestKey)
      if (existing) return HttpResponse.json(azoaOk({ ...find(existing)!, resumed: true }))
      const intent: FiatCheckoutIntent = {
        intentId: crypto.randomUUID(), admissionKey: body.requestKey, status: 'awaiting_payment', settlementState: 'not_started',
        paymentUrl: 'https://checkout.stripe.com/pay/slice', resumed: false, amountPaidMinorUnits: null, currency: 'usd',
        expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      }
      byKey.set(body.requestKey, intent.intentId)
      intents.push(intent)
      return HttpResponse.json(azoaOk(intent))
    }),
  ]

  return {
    handlers,
    get intents() { return intents },
    get successUrls() { return successUrls },
    settle(intentId) {
      const index = intents.findIndex(intent => intent.intentId === intentId)
      const intent = intents[index]
      intents[index] = { ...intent, status: 'complete', settlementState: 'funds_applied', paymentUrl: null, amountPaidMinorUnits: 25_00 }
    },
  }
}

/** The account reads made by the direct first-party money transport. */
export function firstPartyMoneyAccountApi(userId: string): { handlers: HttpHandler[] } {
  const stamp = new Date().toISOString()
  return { handlers: [
    http.get(apiUrl('/api/kyc/status'), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk({
      id: 'money-kyc', userId, tenantId: null, provider: 'IDENTOMAT', providerKey: 'identomat', status: 'APPROVED',
      rejectionReason: null, providerSessionId: 'money-session', hostedVerification: true,
      verificationUrl: null, sessionInstructions: null, submittedAt: stamp, reviewedAt: stamp, expiresAt: null,
      createdDate: stamp, modifiedDate: stamp,
    }))),
    http.get(apiUrl('/api/wallet'), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk([]))),
  ] }
}
