import { randomUUID } from 'node:crypto'
import type { Server } from 'node:https'
import type { E2eEnvironment } from '../environment'
import { listenHttps, respond, type VendorRequest, type VendorResponse } from './http'

export interface StripeCheckoutSessionRecord {
  id: string
  object: 'checkout.session'
  url: string | null
  status: 'open' | 'complete' | 'expired'
  payment_status: 'unpaid' | 'paid'
  payment_intent: string | null
  amount_total: number
  currency: string
  livemode: false
  expires_at: number
  client_reference_id: string
  metadata: Record<string, string>
}

/**
 * Behavioral fake of the two Stripe endpoints the rail calls (`POST/GET /v1/checkout/sessions`)
 * plus a `/__e2e/*` control plane. Mirrors
 * `tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/StripeVendor.cs`. See `e2e/AGENTS.md`.
 */
export async function startStripe(environment: E2eEnvironment): Promise<Server> {
  const sessions = new Map<string, StripeCheckoutSessionRecord>()
  const idempotentCreates = new Map<string, { body: string; sessionId: string }>()
  const origin = `https://${environment.vendorHost}:${environment.stripePort}`

  const handle = (request: VendorRequest): VendorResponse => {
    if (request.path.startsWith('/__e2e/')) return control(request)
    if (request.headers.authorization !== `Bearer ${environment.stripeSecretKey}`)
      return respond(401, { error: { type: 'invalid_request_error' } })

    if (request.method === 'POST' && request.path === '/v1/checkout/sessions') {
      // Same key + same parameters replays the session; same key + different parameters is refused.
      const key = request.headers['idempotency-key']
      const existing = typeof key === 'string' ? idempotentCreates.get(key) : undefined
      if (existing)
        return existing.body === request.body
          ? respond(200, sessions.get(existing.sessionId))
          : respond(400, {
              error: {
                type: 'idempotency_error',
                message: 'Keys for idempotent requests can only be used with the same parameters they were first used with.',
              },
            })
      const form = new URLSearchParams(request.body)
      const metadata: Record<string, string> = {}
      form.forEach((value, name) => {
        const match = /^metadata\[(.+)\]$/.exec(name)
        if (match) metadata[match[1]] = value
      })
      const id = `cs_e2e_${randomUUID().replace(/-/g, '')}`
      const session: StripeCheckoutSessionRecord = {
        id,
        object: 'checkout.session',
        url: `${origin}/checkout/${id}`,
        status: 'open',
        payment_status: 'unpaid',
        payment_intent: null,
        amount_total: Number(form.get('line_items[0][price_data][unit_amount]') ?? 0),
        currency: form.get('line_items[0][price_data][currency]') ?? 'usd',
        livemode: false,
        expires_at: Number(form.get('expires_at') ?? Math.floor(Date.now() / 1000) + 1800),
        client_reference_id: form.get('client_reference_id') ?? '',
        metadata,
      }
      sessions.set(id, session)
      if (typeof key === 'string') idempotentCreates.set(key, { body: request.body, sessionId: id })
      return respond(200, session)
    }

    const read = /^\/v1\/checkout\/sessions\/([^/]+)$/.exec(request.path)
    if (request.method === 'GET' && read) {
      const session = sessions.get(decodeURIComponent(read[1]))
      return session ? respond(200, session) : respond(404, { error: { type: 'invalid_request_error', code: 'resource_missing' } })
    }
    return respond(404, { error: { type: 'invalid_request_error' } })
  }

  /** Test-runner control plane: find a session by intent, complete or expire it. */
  const control = (request: VendorRequest): VendorResponse => {
    if (request.method === 'GET' && request.path === '/__e2e/checkout-sessions') {
      const reference = request.query.get('clientReferenceId')
      return respond(200, Array.from(sessions.values()).filter((session) => !reference || session.client_reference_id === reference))
    }
    const action = /^\/__e2e\/checkout-sessions\/([^/]+)\/(complete|expire)$/.exec(request.path)
    if (request.method === 'POST' && action) {
      const session = sessions.get(action[1])
      if (!session) return respond(404)
      if (action[2] === 'complete') {
        session.status = 'complete'
        session.payment_status = 'paid'
        session.payment_intent = session.payment_intent ?? `pi_e2e_${randomUUID().replace(/-/g, '')}`
      } else {
        session.status = 'expired'
      }
      session.url = null
      return respond(200, session)
    }
    return respond(404)
  }

  return listenHttps(environment.stripePort, environment.generatedDirectory, handle)
}
