import { randomUUID } from 'node:crypto'
import type { Browser, BrowserContext, Page, Response } from '@playwright/test'
import { expect, test } from '../support/fixtures'
import type { AzoaApi, Tenant, TenantConsentGrant } from '../support/api'
import type { E2eEnvironment } from '../support/environment'
import { MemberHandoffApi, type HandoffView, type PrivateInvitation } from '../support/member-handoff'

process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1'
test.use({ trace: 'off', screenshot: 'off', video: 'off' })

const scopes = ['kyc:read', 'kyc:submit', 'units:issue', 'swap:sign', 'transfer:sign'].sort()
const activeExplicit = (grants: TenantConsentGrant[]) => grants.filter(grant => grant.origin === 'UserExplicit' && grant.revokedAt === null)
const privateForms = (tokens: string[]) => tokens.filter(Boolean).flatMap(token => [token, encodeURIComponent(token)])

interface Privacy {
  scrubbedBeforeApi: boolean[]
  leakedUrlOrConsole: boolean
  claims: number
}

async function privatePage(browser: Browser, environment: E2eEnvironment, tokens: string[]) {
  const context = await browser.newContext({ baseURL: environment.appUrl })
  const page = await context.newPage()
  const privacy: Privacy = { scrubbedBeforeApi: [], leakedUrlOrConsole: false, claims: 0 }
  page.on('request', request => {
    const url = new URL(request.url())
    if (url.pathname === '/api/user/claim' && request.method() === 'POST') privacy.claims++
    const exposed = request.url() + (request.headers().referer ?? '')
    if (privateForms(tokens).some(token => exposed.includes(token))) privacy.leakedUrlOrConsole = true
  })
  page.on('console', message => {
    if (privateForms(tokens).some(token => message.text().includes(token))) privacy.leakedUrlOrConsole = true
  })
  await page.route(url => url.origin === new URL(environment.apiUrl).origin && url.pathname.startsWith('/api/user/handoffs'), async route => {
    privacy.scrubbedBeforeApi.push(await page.evaluate(() => window.location.hash === '' && window.location.search === ''))
    await route.continue()
  })
  await context.route(environment.handoffReturnUrl, async route => {
    await route.fulfill({ contentType: 'text/html', body: '<main><h1>Consumer return received</h1></main>' })
  })
  return { context, page, privacy }
}

async function navigatePrivate(page: Page, url: string): Promise<void> {
  const target = new URL(url)
  try {
    await page.goto('about:blank')
    await Promise.all([
      page.waitForURL(candidate => candidate.origin === target.origin && candidate.pathname === target.pathname, { waitUntil: 'domcontentloaded' }),
      page.evaluate(destination => window.location.assign(destination), url),
    ])
  }
  catch { throw new Error('Private handoff navigation failed; invitation omitted.') }
}

async function fillPrivatePassword(page: Page, password: string): Promise<void> {
  await page.getByLabel('Password', { exact: true }).evaluate((element, value) => {
    if (!(element instanceof HTMLInputElement)) throw new Error('Expected the member password input.')
    const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
    if (!set) throw new Error('Password input cannot receive native input.')
    element.focus()
    set.call(element, value)
    element.dispatchEvent(new Event('input', { bubbles: true }))
    element.dispatchEvent(new Event('change', { bubbles: true }))
  }, password)
}

function responseFor(page: Page, environment: E2eEnvironment, path: string, method: string) {
  const pending = page.waitForResponse(response => {
    const url = new URL(response.url())
    return url.origin === new URL(environment.apiUrl).origin && url.pathname === path && response.request().method() === method
  })
  void pending.catch(() => {})
  return pending
}

async function view(response: Response): Promise<HandoffView> {
  expect(response.status(), 'the member API accepted the exact saved handoff').toBe(200)
  const body = await response.json() as { isError: boolean; result: HandoffView }
  expect(body.isError).toBe(false)
  return body.result
}

async function assertPrivacy(context: BrowserContext, privacy: Privacy, tokens: string[]) {
  expect(privacy.scrubbedBeforeApi.length > 0, 'browser made a real handoff API request').toBe(true)
  expect(privacy.scrubbedBeforeApi.every(Boolean), 'fragment was scrubbed before preview/read/decision requests continued').toBe(true)
  expect(privacy.leakedUrlOrConsole, 'raw invitation stayed out of request URLs, referrers and console').toBe(false)
  const storage = JSON.stringify(await context.storageState())
  expect(privateForms(tokens).some(token => storage.includes(token)), 'raw or encoded invitation stayed out of browser persistence').toBe(false)
  for (const page of context.pages()) {
    const leaked = await page.evaluate(secrets => {
      const entries = Object.entries(sessionStorage).flat()
      return secrets.some(secret => secret && entries.some(entry => entry.includes(secret)))
    }, privateForms(tokens))
    expect(leaked, 'raw invitation stayed out of session storage').toBe(false)
  }
}

async function claimThroughUi(page: Page, environment: E2eEnvironment, invitation: PrivateInvitation, id: string, member: string, password: string) {
  const preview = responseFor(page, environment, '/api/user/handoffs/preview', 'POST')
  await navigatePrivate(page, invitation.url)
  const descriptor = await view(await preview)
  expect(descriptor.requestId).toBe(id)
  expect(descriptor.targetUserId).toBe(member)
  expect([...descriptor.scopes].sort()).toEqual(scopes)
  const consent = responseFor(page, environment, `/api/user/handoffs/${id}`, 'GET')
  await fillPrivatePassword(page, password)
  await page.getByRole('button', { name: 'Claim account', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Your permission', exact: true })).toBeVisible()
  const owned = await view(await consent)
  expect(owned.requestId).toBe(id)
  expect(owned.targetUserId).toBe(member)
  await expect.poll(() => new URL(page.url()).pathname === '/consent' && new URL(page.url()).hash === '', { message: 'claimed member reaches a scrubbed consent page' }).toBe(true)
  return owned
}

async function approveThroughUi(page: Page, environment: E2eEnvironment, api: AzoaApi, tenant: Tenant, descriptor: HandoffView) {
  expect(activeExplicit(await api.tenantConsentGrants(tenant))).toHaveLength(0)
  const approval = responseFor(page, environment, `/api/user/handoffs/${descriptor.requestId}/approve`, 'POST')
  await page.getByRole('button', { name: 'Approve permission', exact: true }).click()
  const response = await approval
  expect(Object.keys(response.request().postDataJSON() as object).length === 0, 'approval only names the saved request, never editable terms').toBe(true)
  const approved = await view(response)
  expect(approved.status).toBe('approved')
  expect(approved.requestId).toBe(descriptor.requestId)
  expect(approved.targetUserId).toBe(descriptor.targetUserId)
  expect(approved.tenantId).toBe(tenant.userId)
  expect(approved.returnUrl).toBe(environment.handoffReturnUrl)
  expect(approved.grantExpiresAt).toBe(descriptor.grantExpiresAt)
  expect([...approved.scopes].sort()).toEqual(scopes)
  const grants = activeExplicit(await api.tenantConsentGrants(tenant))
  expect(grants).toHaveLength(1)
  expect([...grants[0].scopes].sort()).toEqual(scopes)
  expect(Date.parse(grants[0].expiresAt!)).toBe(Date.parse(descriptor.grantExpiresAt))
  await expect(page.getByRole('button', { name: 'Set up bank', exact: true })).toBeVisible()
  return grants[0]
}

async function returnThroughUi(page: Page, environment: E2eEnvironment) {
  const link = page.getByRole('link', { name: 'Return to platform', exact: true })
  expect((await link.getAttribute('href')) === environment.handoffReturnUrl, 'return uses only the saved fixed consumer URL').toBe(true)
  await link.click()
  await expect(page.getByRole('heading', { name: 'Consumer return received' })).toBeVisible()
  expect(page.url() === environment.handoffReturnUrl, 'consumer receives no invitation fragment or query').toBe(true)
}

async function actor(api: AzoaApi, handoffs: MemberHandoffApi) {
  const tenant = await api.makeTenant(await api.registerPerson('handoff'))
  const subject = `member-${randomUUID().replace(/-/g, '')}`
  const member = await api.provisionMember(tenant, subject)
  await handoffs.waitForIdentityReadiness()
  return { tenant, subject, member, password: `Member-${randomUUID()}-Pass1` }
}

test.describe('hosted member handoff', () => {
  test.setTimeout(180_000)

  test('operator resend retires the old link; browser claims and approves exact permission before fixed return', async ({ browser, request, api, environment }) => {
    const handoffs = new MemberHandoffApi(request, api, environment)
    const person = await actor(api, handoffs)
    const issued = await handoffs.issue(person.tenant, person.subject)
    const old = await handoffs.invitation(issued.requestId, person.member, true)
    const resent = await handoffs.resend(person.tenant, person.subject, issued.requestId)
    expect(resent.requestId).toBe(issued.requestId)
    expect(resent.expiresAt).toBe(issued.expiresAt)
    expect(resent.targetUserId).toBe(issued.targetUserId)
    const invitation = await handoffs.invitation(resent.requestId, person.member, true)
    expect(invitation.token !== old.token, 'resend replaces the private token').toBe(true)
    const tokens = [old.token, invitation.token]
    const member = await privatePage(browser, environment, tokens)
    try {
      const refused = responseFor(member.page, environment, '/api/user/handoffs/preview', 'POST')
      await navigatePrivate(member.page, old.url)
      expect((await refused).status(), 'retired invitation cannot preview').toBe(404)
      await expect(member.page.getByRole('alert').filter({ hasText: 'expired or was replaced' })).toBeVisible()
      const descriptor = await claimThroughUi(member.page, environment, invitation, issued.requestId, person.member, person.password)
      expect(await handoffs.memberOwnership(person.tenant, person.subject)).toBe('claimed')
      await approveThroughUi(member.page, environment, api, person.tenant, descriptor)
      expect(member.privacy.claims).toBe(1)
      await assertPrivacy(member.context, member.privacy, tokens)
      await returnThroughUi(member.page, environment)
    } finally { await member.context.close() }
  })

  test('lost claim reply after server commit recovers by owner login and approves without claiming again', async ({ browser, request, api, environment }) => {
    const handoffs = new MemberHandoffApi(request, api, environment)
    const person = await actor(api, handoffs)
    const issued = await handoffs.issue(person.tenant, person.subject)
    const invitation = await handoffs.invitation(issued.requestId, person.member, true)
    const member = await privatePage(browser, environment, [invitation.token])
    let committedBeforeAbort = false
    let recoveredWithSameCredential = false
    const recoveredLogin = responseFor(member.page, environment, '/api/user/login', 'POST')
    await member.page.route(new URL('/api/user/claim', environment.apiUrl).href, async route => {
      const response = await route.fetch()
      expect(response.status(), 'the claim committed before losing its reply').toBe(200)
      expect(await handoffs.memberOwnership(person.tenant, person.subject)).toBe('claimed')
      committedBeforeAbort = true
      await response.dispose()
      await route.abort('failed')
    })
    member.page.on('request', request => {
      if (new URL(request.url()).pathname === '/api/user/login') {
        const body = request.postDataJSON() as { userId?: string; password?: string }
        recoveredWithSameCredential = body.userId === person.member && body.password === person.password
      }
    })
    try {
      const descriptor = await claimThroughUi(member.page, environment, invitation, issued.requestId, person.member, person.password)
      expect((await recoveredLogin).status(), 'real stable-identity login succeeds after the committed claim reply is lost').toBe(200)
      expect(committedBeforeAbort).toBe(true)
      expect(recoveredWithSameCredential, 'recovery used the chosen password and stable owner identity').toBe(true)
      await approveThroughUi(member.page, environment, api, person.tenant, descriptor)
      expect(member.privacy.claims, 'login recovery never starts a second claim').toBe(1)
      await assertPrivacy(member.context, member.privacy, [invitation.token])
      await returnThroughUi(member.page, environment)
    } finally { await member.context.close() }
  })

  test('claimed renewal has no claim token; fresh login can decline while preserving earlier permission', async ({ browser, request, api, environment }) => {
    const handoffs = new MemberHandoffApi(request, api, environment)
    const person = await actor(api, handoffs)
    const issued = await handoffs.issue(person.tenant, person.subject)
    const invitation = await handoffs.invitation(issued.requestId, person.member, true)
    const first = await privatePage(browser, environment, [invitation.token])
    let original: TenantConsentGrant | null = null
    try {
      const descriptor = await claimThroughUi(first.page, environment, invitation, issued.requestId, person.member, person.password)
      original = await approveThroughUi(first.page, environment, api, person.tenant, descriptor)
      await assertPrivacy(first.context, first.privacy, [invitation.token])
    } finally { await first.context.close() }
    const renewal = await handoffs.issue(person.tenant, person.subject)
    expect(renewal.ownership).toBe('claimed')
    const consentOnly = await handoffs.invitation(renewal.requestId, person.member, false)
    const fresh = await privatePage(browser, environment, [invitation.token])
    try {
      const unsigned = responseFor(fresh.page, environment, `/api/user/handoffs/${renewal.requestId}`, 'GET')
      await navigatePrivate(fresh.page, consentOnly.url)
      expect((await unsigned).status(), 'a fresh browser must authenticate before reading the renewal').toBe(401)
      await expect(fresh.page.getByRole('alert').filter({ hasText: 'Sign in to review this request' })).toBeVisible()
      await expect(fresh.page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible()
      await fresh.page.getByLabel('Account reference', { exact: true }).fill(person.member)
      await fillPrivatePassword(fresh.page, person.password)
      const signedIn = responseFor(fresh.page, environment, '/api/user/login', 'POST')
      const read = responseFor(fresh.page, environment, `/api/user/handoffs/${renewal.requestId}`, 'GET')
      await fresh.page.getByRole('button', { name: 'Sign in', exact: true }).click()
      expect((await signedIn).status(), 'the fresh owner authenticates through real stable-identity HTTP login').toBe(200)
      const descriptor = await view(await read)
      expect(descriptor.ownership).toBe('claimed')
      expect([...descriptor.scopes].sort()).toEqual(scopes)
      const declined = responseFor(fresh.page, environment, `/api/user/handoffs/${renewal.requestId}/decline`, 'POST')
      await fresh.page.getByRole('button', { name: 'Decline', exact: true }).click()
      expect((await view(await declined)).status).toBe('declined')
      await expect(fresh.page.getByRole('status')).toContainText('Other existing permissions are managed separately')
      const grants = activeExplicit(await api.tenantConsentGrants(person.tenant))
      expect(grants).toHaveLength(1)
      expect(grants[0].grantId).toBe(original!.grantId)
      expect(grants[0].expiresAt).toBe(original!.expiresAt)
      expect([...grants[0].scopes].sort()).toEqual([...original!.scopes].sort())
      expect(fresh.privacy.claims, 'renewal only signs in; it never claims ownership again').toBe(0)
      await assertPrivacy(fresh.context, fresh.privacy, [invitation.token])
      await returnThroughUi(fresh.page, environment)
    } finally { await fresh.context.close() }
  })
})
