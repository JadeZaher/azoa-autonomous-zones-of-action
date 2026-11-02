import { expect, type APIRequestContext, type APIResponse } from '@playwright/test'
import type { AzoaApi, Tenant } from './api'
import type { E2eEnvironment } from './environment'

export interface HandoffReceipt {
  requestId: string
  targetUserId: string
  ownership: 'custodial' | 'claimed'
  status: string
  expiresAt: string
  delivery: string
}

export interface HandoffView {
  requestId: string
  targetUserId: string
  tenantId: string
  tenantName: string
  ownership: string
  scopes: string[]
  grantExpiresAt: string
  returnUrl: string
  expiresAt: string
  status: string
}

export interface PrivateInvitation { url: string; token: string }

/** Public API setup and read-only oracles; sensitive responses never enter assertion messages. */
export class MemberHandoffApi {
  constructor(private request: APIRequestContext, private api: AzoaApi, private environment: E2eEnvironment) {}

  async waitForIdentityReadiness(): Promise<void> {
    const headers = await this.operatorHeaders()
    await expect.poll(async () => {
      const response = await this.request.get(this.url('/api/operator/wallet-identities'), { headers })
      expect(response.status(), 'wallet identity status is readable by the operator').toBe(200)
      return (await response.json() as { status: string }).status
    }, { timeout: 30_000, intervals: [1000] }).toBe('Ready')
  }

  async issue(tenant: Tenant, subject: string): Promise<HandoffReceipt> {
    return this.metadata(await this.request.post(this.base(subject), {
      headers: { 'X-Api-Key': tenant.apiKey }, data: this.terms(),
    }))
  }

  async resend(tenant: Tenant, subject: string, id: string): Promise<HandoffReceipt> {
    let receipt: HandoffReceipt | null = null
    await expect.poll(async () => {
      const response = await this.request.post(`${this.base(subject)}/${encodeURIComponent(id)}/resend`, {
        headers: { 'X-Api-Key': tenant.apiKey }, data: this.terms(),
      })
      if (response.status() === 429) return false
      receipt = await this.metadata(response)
      return true
    }, { message: 'resend succeeds after the real one-minute cooldown', timeout: 75_000, intervals: [15_000] }).toBe(true)
    return receipt!
  }

  async invitation(id: string, expectedUser: string, claim: boolean): Promise<PrivateInvitation> {
    const delivery = await this.result<{ text: string }>(await this.request.get(
      this.url(`/api/operator/member-handoff-deliveries/${encodeURIComponent(id)}`), { headers: await this.operatorHeaders() },
    ))
    let url: URL
    try { url = new URL(delivery.text.trim().split('\n').at(-1)!) }
    catch { throw new Error('Operator delivery did not contain a usable private invitation.') }
    const fragment = new URLSearchParams(url.hash.slice(1))
    expect(url.origin === new URL(this.environment.appUrl).origin, 'invitation uses the configured local hosted origin').toBe(true)
    expect(url.pathname === (claim ? '/claim' : '/consent'), 'invitation selects the ownership-appropriate page').toBe(true)
    expect(fragment.get('requestId') === id && fragment.get('targetUserId') === expectedUser, 'invitation binds the exact descriptor and member').toBe(true)
    const token = fragment.get('token') ?? ''
    expect(claim ? token.length > 0 : token.length === 0, 'only an unclaimed member receives a claim token').toBe(true)
    return { url: url.href, token }
  }

  async memberOwnership(tenant: Tenant, subject: string): Promise<string> {
    const value = await this.result<{ ownership: string }>(await this.request.get(
      this.url(`/api/tenant/custodial-accounts/${encodeURIComponent(subject)}`), { headers: { 'X-Api-Key': tenant.apiKey } },
    ))
    return value.ownership
  }

  private terms() { return { deliveryEmail: 'member@e2e.azoa.test', returnUrl: this.environment.handoffReturnUrl } }
  private base(subject: string) { return this.url(`/api/tenant/custodial-accounts/${encodeURIComponent(subject)}/handoff`) }
  private url(path: string) { return new URL(path, this.environment.apiUrl).href }
  private async operatorHeaders() { return { Authorization: `Bearer ${await this.api.operatorToken()}` } }

  private async metadata(response: APIResponse): Promise<HandoffReceipt> {
    const value = await this.result<HandoffReceipt>(response)
    const expected = ['delivery', 'expiresAt', 'ownership', 'requestId', 'status', 'targetUserId'].sort().join('|')
    expect(Object.keys(value).sort().join('|') === expected, 'tenant receipt contains metadata only, with no token, link or recipient').toBe(true)
    expect(value.delivery).toBe('operator_stub')
    return value
  }

  private async result<T>(response: APIResponse): Promise<T> {
    expect(response.status(), 'handoff API request succeeded; sensitive response omitted').toBe(200)
    const envelope = await response.json() as { isError: boolean; result: T }
    expect(envelope.isError, 'handoff API returned success').toBe(false)
    return envelope.result
  }
}
