import { createHmac, randomUUID } from 'node:crypto'
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test'
import type { E2eEnvironment } from './environment'

/** AZOAResult envelope as the .NET controllers serialize it. */
interface Envelope<T> {
  isError: boolean
  message: string
  result: T
}

export interface Person {
  userId: string
  username: string
  email: string
  password: string
}

/** A tenant is a person holding the operator-issued tenant key (`POST api/apikey/tenant`). */
export interface Tenant extends Person {
  apiKey: string
}

/** The node's verified platform unit (`GET api/admin/platform-token` → `definition`). */
export interface PlatformUnit {
  chainType: string
  network: string
  assetId: string
  decimals: number
}

/** A durable fungible-creation operation as `api/admin/platform-token/creation/{id}` renders it. */
interface CreationOperation {
  operationId: string
  version: number
  state: string
  quote: { quoteId: string } | null
}

/** Mirrors `tools/dev-node/dev-node-up.ps1` §7 (`$ArdaGenesis*`, `$ArdaOnRamp*`); see `e2e/AGENTS.md` §settlement. */
const platformUnitChain = 'Simulated'
const platformUnitNetwork = 'Devnet'
const onRampPartition = 'on-ramp'
const onRampBudgetBaseUnits = '1000000000'

/**
 * The stack's public HTTP surface, driven the way an operator, a tenant integration and a vendor
 * would. Only routes that exist in the API; no direct database access. See `e2e/AGENTS.md`.
 */
export class AzoaApi {
  private operatorAccessToken: string | null = null

  constructor(
    private readonly request: APIRequestContext,
    private readonly environment: E2eEnvironment,
  ) {}

  /** Registers a person through `api/user/register` and returns credentials plus the new id. */
  async registerPerson(label: string): Promise<Person> {
    const suffix = randomUUID().replace(/-/g, '').slice(0, 10)
    const person = { username: `${label}${suffix}`.slice(0, 24), email: `${label}-${suffix}@e2e.azoa.test`, password: `E2e-${suffix}-Pass1` }
    const registered = await this.ok<{ id: string }>(await this.request.post('/api/user/register', { data: person }))
    return { ...person, userId: registered.id }
  }

  async operatorToken(): Promise<string> {
    if (this.operatorAccessToken) return this.operatorAccessToken
    const session = await this.ok<{ accessToken: string }>(
      await this.request.post('/api/operator/session', {
        data: { username: this.environment.operatorUsername, password: this.environment.operatorPassword },
      }),
    )
    this.operatorAccessToken = session.accessToken
    return session.accessToken
  }

  /** The operator issues the fixed-scope tenant integration key to an existing person. */
  async makeTenant(person: Person): Promise<Tenant> {
    const issued = await this.ok<{ key: string }>(
      await this.request.post('/api/apikey/tenant', {
        headers: await this.operatorHeaders(),
        data: { tenantUserId: person.userId, name: 'e2e-tenant' },
      }),
    )
    return { ...person, apiKey: issued.key }
  }

  /** The operator enables the Identomat profile and assigns it as the tenant's KYC policy. */
  async assignIdentomat(tenant: Tenant): Promise<void> {
    const headers = await this.operatorHeaders()
    const profile = await this.request.put('/api/operator/kyc/providers/identomat', {
      headers,
      data: { displayName: 'Identomat', adapterKey: 'identomat', enabled: true, policyVersion: 'identomat-hosted-v1', assuranceLevel: 'document-and-liveness' },
    })
    expect([200, 409], await profile.text()).toContain(profile.status())
    await this.ok(await this.request.put(`/api/operator/tenants/${tenant.userId}/kyc-provider`, { headers, data: { providerKey: 'identomat' } }))
  }

  /** Enrols a custodial member and returns its Azoa user id. */
  async enrolMember(tenant: Tenant, subject: string): Promise<string> {
    const account = await this.ok<{ userId: string }>(
      await this.request.put(`/api/tenant/custodial-accounts/${subject}`, { headers: this.tenantHeaders(tenant, `enrol-${subject}`) }),
    )
    return account.userId
  }

  /** Provisions a claimable member using its server-returned identity and a future owner password. */
  async provisionMember(tenant: Tenant, subject: string): Promise<Person> {
    const member = await this.ok<{ userId: string; externalUserId: string; username: string; email: string }>(
      await this.request.post('/api/tenant/users', {
        headers: this.tenantHeaders(tenant), data: { externalUserId: subject },
      }),
    )
    expect(member.externalUserId).toBe(subject)
    expect(member.email).toBeTruthy()
    return { userId: member.userId, username: member.username, email: member.email, password: `E2e-${randomUUID()}-Pass1` }
  }

  /** Claims that exact member through a real tenant invitation and user-chosen password. */
  async claimMember(tenant: Tenant, member: Person): Promise<void> {
    const invite = await this.ok<{ claimToken: string; targetUserId: string }>(
      await this.request.post('/api/user/claim-invite', {
        headers: this.tenantHeaders(tenant), data: { childUserId: member.userId },
      }),
    )
    expect(invite.targetUserId).toBe(member.userId)
    const claimed = await this.ok<{ userId: string }>(await this.request.post('/api/user/claim', {
      data: { targetUserId: member.userId, claimToken: invite.claimToken, newPassword: member.password },
    }))
    expect(claimed.userId).toBe(member.userId)
  }

  async beginMemberKyc(tenant: Tenant, subject: string): Promise<{ verificationUrl: string }> {
    return this.ok(
      await this.request.post(`/api/tenant/custodial-accounts/${subject}/kyc/session`, {
        headers: this.tenantHeaders(tenant, `kyc-session-${subject}`),
      }),
    )
  }

  async memberKycStatus(tenant: Tenant, subject: string): Promise<string> {
    const account = await this.ok<{ kycStatus: string }>(
      await this.request.get(`/api/tenant/custodial-accounts/${subject}`, { headers: this.tenantHeaders(tenant) }),
    )
    return account.kycStatus
  }

  /** Identomat's session-change callback, as the vendor posts it (anonymous, key in the body). */
  async postIdentomatCallback(sessionId: string, claimedResult: string): Promise<void> {
    const response = await this.request.post('/api/kyc/callback/identomat', {
      data: { callbackApiKey: this.environment.identomatCallbackKey, type: 'session-change', sessionId, result: claimedResult },
    })
    expect(response.status(), await response.text()).toBe(202)
  }

  /**
   * The operator's one-time platform-unit genesis on the Simulated chain, driven through its own
   * operation's quote → accept → continue → refresh routes, then the `on-ramp` reserve budget.
   * Idempotent: an existing unit short-circuits genesis and re-declaring SETS the budget.
   */
  async ensurePlatformUnit(): Promise<PlatformUnit> {
    const headers = await this.operatorHeaders()
    if (!(await this.platformUnit(headers))) {
      const genesis = await this.request.post('/api/admin/platform-token/genesis', {
        headers, data: { chainType: platformUnitChain, expectedNetwork: platformUnitNetwork, confirmIrreversible: true },
      })
      // 409: genesis already completed (a concurrent or earlier run); the read below decides.
      if (genesis.status() !== 409) {
        let operation = (await this.accepted<{ operation: CreationOperation | null }>(genesis)).operation
        for (let step = 0; operation && operation.state !== 'Completed' && step < 12; step++) {
          const base = `/api/admin/platform-token/creation/${operation.operationId}`
          const next =
            operation.state === 'AwaitingConsent'
              ? operation.quote
                ? await this.request.post(`${base}/accept-quote`, { headers, data: { version: operation.version, quoteId: operation.quote.quoteId } })
                : await this.request.post(`${base}/quote`, { headers })
              : operation.state === 'Admitted'
                ? await this.request.post(`${base}/continue`, { headers, data: { requestId: randomUUID(), version: operation.version } })
                : await this.request.post(`${base}/refresh`, { headers })
          operation = await this.accepted<CreationOperation>(next)
          expect(['Cancelled', 'FailedNoEffect'], `platform-unit genesis ended ${operation.state}`).not.toContain(operation.state)
        }
        expect(operation?.state ?? 'Completed', 'platform-unit genesis must complete').toBe('Completed')
      }
    }
    const unit = await this.platformUnit(headers)
    expect(unit, 'genesis must leave a readable, verified platform unit').not.toBeNull()
    expect(unit!.chainType).toBe(platformUnitChain)

    const declared = await this.ok<{ partitions: { name: string; declared: string }[] }>(
      await this.request.post('/api/admin/platform-token/partitions', {
        headers, data: { partitions: { [onRampPartition]: onRampBudgetBaseUnits } },
      }),
    )
    expect(declared.partitions.find((partition) => partition.name === onRampPartition)?.declared).toBe(onRampBudgetBaseUnits)
    return unit!
  }

  /** The tenant registers its own exact USD price for the platform unit (base units per cent). */
  async registerTenantPrice(tenant: Tenant, unit: PlatformUnit, unitsPerMinorUnit: string): Promise<void> {
    const price = await this.ok<{ assetId: string; unitsPerMinorUnit: string; status: string }>(
      await this.request.post('/api/fiat/price-schedule', {
        headers: this.tenantHeaders(tenant),
        data: { chainType: unit.chainType, assetId: unit.assetId, unitsPerMinorUnit, source: 'e2e-journey' },
      }),
    )
    expect(price).toMatchObject({ assetId: unit.assetId, unitsPerMinorUnit, status: 'Active' })
  }

  /** The tenant starts a funding checkout for a member it is authorized to act for. */
  async startCheckout(tenant: Tenant, memberUserId: string, amountMinorUnits: number): Promise<{ intentId: string; paymentUrl: string }> {
    return this.ok(
      await this.request.post(`/api/fiat/checkout/${memberUserId}`, {
        headers: this.tenantHeaders(tenant),
        data: { requestKey: randomUUID(), amountMinorUnits, purpose: 'funds-top-up' },
      }),
    )
  }

  async checkoutIntent(tenant: Tenant, memberUserId: string, intentId: string): Promise<{ status: string; settlementState: string }> {
    return this.ok(await this.request.get(`/api/fiat/checkout/${memberUserId}/${intentId}`, { headers: this.tenantHeaders(tenant) }))
  }

  /**
   * Delivers a Stripe event signed with the endpoint secret the API was configured with, using
   * Stripe's documented `t=…,v1=HMAC-SHA256("{t}.{payload}")` scheme.
   */
  async deliverStripeEvent(payload: object): Promise<APIResponse> {
    const body = JSON.stringify(payload)
    const timestamp = Math.floor(Date.now() / 1000)
    const signature = createHmac('sha256', this.environment.stripeWebhookSecret).update(`${timestamp}.${body}`).digest('hex')
    return this.request.post('/api/fiat/webhook/stripe', {
      headers: { 'Content-Type': 'application/json', 'Stripe-Signature': `t=${timestamp},v1=${signature}` },
      data: body,
    })
  }

  /** The verified platform unit, or null while genesis has not run (404). */
  private async platformUnit(headers: Record<string, string>): Promise<PlatformUnit | null> {
    const state = await this.request.get('/api/admin/platform-token', { headers })
    if (state.status() === 404) return null
    const { definition } = await this.ok<{ definition: PlatformUnit }>(state)
    return definition.assetId ? definition : null
  }

  private async operatorHeaders(): Promise<Record<string, string>> {
    return { Authorization: `Bearer ${await this.operatorToken()}` }
  }

  private tenantHeaders(tenant: Tenant, idempotencyKey?: string): Record<string, string> {
    return idempotencyKey ? { 'X-Api-Key': tenant.apiKey, 'Idempotency-Key': idempotencyKey } : { 'X-Api-Key': tenant.apiKey }
  }

  private async ok<T>(response: APIResponse): Promise<T> {
    const text = await response.text()
    expect(response.status(), `${response.url()} → ${text}`).toBe(200)
    const envelope = JSON.parse(text) as Envelope<T>
    expect(envelope.isError, text).toBe(false)
    return envelope.result
  }

  /** As `ok`, for durable-operation routes that answer 202 until the operation is terminal. */
  private async accepted<T>(response: APIResponse): Promise<T> {
    const text = await response.text()
    expect([200, 202], `${response.url()} → ${text}`).toContain(response.status())
    const envelope = JSON.parse(text) as Envelope<T>
    expect(envelope.isError, text).toBe(false)
    return envelope.result
  }
}
