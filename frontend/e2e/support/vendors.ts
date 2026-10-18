import { expect, type APIRequestContext, type BrowserContext, type PlaywrightWorkerArgs } from '@playwright/test'
import type { E2eEnvironment } from './environment'
import type { IdentomatSessionRecord, IdentomatVerdict, StripeCheckoutSessionRecord } from './fake-vendors'

/**
 * The journeys' handle on the fake vendors: their `/__e2e/*` control planes plus the browser-side
 * interception of the hosted pages the real vendors would serve. See `e2e/AGENTS.md` §hosted-pages.
 */
export interface WebhookDelivery {
  path: string
  headers: Record<string, string>
  body: string
  receivedAt: string
}

export class FakeVendors {
  private constructor(
    private readonly identomat: APIRequestContext,
    private readonly stripe: APIRequestContext,
    private readonly webhookReceiver: APIRequestContext,
    readonly environment: E2eEnvironment,
  ) {}

  static async connect(playwright: PlaywrightWorkerArgs['playwright'], environment: E2eEnvironment): Promise<FakeVendors> {
    // The control planes use the per-run test certificate; only this test-side client skips verification.
    const identomat = await playwright.request.newContext({
      baseURL: `https://127.0.0.1:${environment.identomatPort}`,
      ignoreHTTPSErrors: true,
    })
    const stripe = await playwright.request.newContext({
      baseURL: `https://127.0.0.1:${environment.stripePort}`,
      ignoreHTTPSErrors: true,
    })
    const webhookReceiver = await playwright.request.newContext({
      baseURL: `https://127.0.0.1:${environment.webhookReceiverPort}`,
      ignoreHTTPSErrors: true,
    })
    return new FakeVendors(identomat, stripe, webhookReceiver, environment)
  }

  async dispose(): Promise<void> {
    await Promise.all([this.identomat.dispose(), this.stripe.dispose(), this.webhookReceiver.dispose()])
  }

  /** Every delivery the `webhook-receiver` service accepted, oldest first (headers limited to `x-azoa-*`). */
  async webhookDeliveries(): Promise<WebhookDelivery[]> {
    return (await (await this.webhookReceiver.get('/__e2e/deliveries')).json()) as WebhookDelivery[]
  }

  async identomatSessionByToken(sessionToken: string): Promise<IdentomatSessionRecord> {
    const sessions = (await (await this.identomat.get('/__e2e/sessions')).json()) as IdentomatSessionRecord[]
    const session = sessions.find((candidate) => candidate.sessionToken === sessionToken)
    expect(session, `Identomat fake has no session for token ${sessionToken}`).toBeDefined()
    return session as IdentomatSessionRecord
  }

  async setIdentomatVerdict(sessionId: string, verdict: IdentomatVerdict): Promise<void> {
    expect((await this.identomat.post(`/__e2e/sessions/${sessionId}/verdict`, { data: { verdict } })).status()).toBe(200)
  }

  async completeCheckout(sessionId: string): Promise<StripeCheckoutSessionRecord> {
    const response = await this.stripe.post(`/__e2e/checkout-sessions/${sessionId}/complete`)
    expect(response.status()).toBe(200)
    return (await response.json()) as StripeCheckoutSessionRecord
  }

  /**
   * Serves the Identomat hosted widget inside the browser. Pressing "Complete verification" is
   * where the person finishes at the vendor: `onComplete` then plays the vendor's side (verdict
   * plus callback) before the page confirms.
   */
  async interceptIdentomatWidget(context: BrowserContext, onComplete: (sessionToken: string) => Promise<void>): Promise<void> {
    const origin = `https://${this.environment.vendorHost}:${this.environment.identomatPort}`
    await context.route(`${origin}/widget/**`, async (route) => {
      const url = new URL(route.request().url())
      const token = url.searchParams.get('session_token') ?? ''
      if (url.pathname.endsWith('/complete')) {
        await onComplete(token)
        await route.fulfill({ contentType: 'text/html', body: hostedPage('Identomat', '<p role="status">Verification submitted</p>') })
        return
      }
      await route.fulfill({
        contentType: 'text/html',
        body: hostedPage(
          'Identomat',
          `<h1>Identity verification (simulated)</h1>
           <form method="get" action="${origin}/widget/complete">
             <input type="hidden" name="session_token" value="${token}" />
             <button type="submit">Complete verification</button>
           </form>`,
        ),
      })
    })
  }

  /** Serves the Stripe hosted checkout inside the browser; "Pay" plays Stripe's side via `onPay`. */
  async interceptStripeCheckout(context: BrowserContext, onPay: (sessionId: string) => Promise<void>): Promise<void> {
    const origin = `https://${this.environment.vendorHost}:${this.environment.stripePort}`
    await context.route(`${origin}/checkout/**`, async (route) => {
      const url = new URL(route.request().url())
      const sessionId = url.pathname.split('/')[2] ?? ''
      if (url.pathname.endsWith('/pay')) {
        await onPay(sessionId)
        await route.fulfill({ contentType: 'text/html', body: hostedPage('Stripe', '<p role="status">Payment received</p>') })
        return
      }
      await route.fulfill({
        contentType: 'text/html',
        body: hostedPage('Stripe', `<h1>Checkout (simulated)</h1><form method="get" action="${origin}/checkout/${sessionId}/pay"><button type="submit">Pay</button></form>`),
      })
    })
  }
}

function hostedPage(vendor: string, body: string): string {
  return `<!doctype html><html><head><title>${vendor} (E2E fake)</title></head><body>${body}</body></html>`
}
