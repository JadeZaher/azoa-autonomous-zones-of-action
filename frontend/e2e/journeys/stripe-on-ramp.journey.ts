import { tenantProvisionedRefPrefix } from '../support/api'
import { completeBusinessVerification, expect, signIn, test } from '../support/fixtures'

/**
 * Stripe test-mode on-ramp: a KYC-approved member pays on the hosted checkout and the node
 * settles the funding intent from Stripe's signed webhook. There is no "Add funds" screen yet, so
 * the tenant integration starts the checkout; the payer's part happens in the browser.
 */
test('a hosted card payment settles the funding intent through the signed webhook', async ({ page, api, vendors }) => {
  // The settlement rail (e2e/AGENTS.md §settlement): a verified platform unit with an on-ramp
  // budget, seeded first while the operator session is fresh (genesis needs a recent sign-in).
  const platformUnit = await api.ensurePlatformUnit()
  const tenant = await api.makeTenant(await api.registerPerson('onramp'))
  await signIn(page, tenant)
  await completeBusinessVerification(page, api, vendors)
  // A KYB-verified tenant prices its own sales: 100 base units per cent (1 unit = $0.01 at 2 decimals).
  await api.registerTenantPrice(tenant, platformUnit, '100')
  await api.assignIdentomat(tenant)
  // The payer is a tenant-custodial member (contract §12.3.2): provisioning writes the implied
  // participation grant, whose credit floor carries units:issue, the scope checkout probes.
  const subject = `payer-${Date.now().toString(36)}`
  const payerId = await api.provisionMember(tenant, subject)
  expect(await api.enrolMember(tenant, subject)).toBe(payerId)

  // Tenant checkout requires this tenant's current identity approval.
  await vendors.interceptIdentomatWidget(page.context(), async (sessionToken) => {
    const session = await vendors.identomatSessionByToken(sessionToken)
    await vendors.setIdentomatVerdict(session.id, 'approved')
    await api.postIdentomatCallback(session.id, 'APPROVED')
  })
  const { verificationUrl } = await api.beginMemberKyc(tenant, subject)
  await page.goto(verificationUrl)
  await page.getByRole('button', { name: 'Complete verification' }).click()
  await expect.poll(() => api.memberKycStatus(tenant, subject), { timeout: 15_000 }).toBe('Approved')

  // A fresh tenant with one provisioned member holds exactly one grant: that member's live implied
  // grant. It must cover the issuance (units:issue) and never a spend (transfer:sign, H4).
  const grants = (await api.tenantConsentGrants(tenant)).filter((grant) => grant.participationRef?.startsWith(tenantProvisionedRefPrefix))
  expect(grants, 'provisioning must write exactly one implied participation grant').toHaveLength(1)
  expect(grants[0]).toMatchObject({ origin: 'Participation', revokedAt: null, expiresAt: null })
  expect(grants[0].scopes).toContain('units:issue')
  expect(grants[0].scopes).not.toContain('transfer:sign')

  const checkout = await api.startCheckout(tenant, payerId, 2_500)
  let paidEventStatus = 0
  await vendors.interceptStripeCheckout(page.context(), async (sessionId) => {
    const paid = await vendors.completeCheckout(sessionId)
    const delivery = await api.deliverStripeEvent({
      id: `evt_e2e_${Date.now()}`,
      object: 'event',
      api_version: '2024-06-20',
      created: Math.floor(Date.now() / 1000),
      livemode: false,
      type: 'checkout.session.completed',
      data: { object: paid },
    })
    paidEventStatus = delivery.status()
  })

  await page.goto(checkout.paymentUrl)
  await page.getByRole('button', { name: 'Pay' }).click()
  await expect(page.getByRole('status')).toHaveText('Payment received')
  expect([200, 202], 'Stripe must be told the delivery was owned, not to retry').toContain(paidEventStatus)

  await expect
    .poll(async () => (await api.checkoutIntent(tenant, payerId, checkout.intentId)).status, { timeout: 15_000 })
    .toBe('complete')
  // funds_applied is projected only from an inbox row the on-ramp handler APPLIED, i.e. the
  // issuance confirmed and the lot is Issued; payment_received alone would be charged-and-uncredited.
  await expect
    .poll(async () => (await api.checkoutIntent(tenant, payerId, checkout.intentId)).settlementState, { timeout: 15_000 })
    .toBe('funds_applied')
})
