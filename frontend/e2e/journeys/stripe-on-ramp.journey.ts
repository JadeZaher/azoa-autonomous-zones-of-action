import { expect, test } from '../support/fixtures'

/**
 * Stripe test-mode on-ramp: a KYC-approved member pays on the hosted checkout and the node
 * settles the funding intent from Stripe's signed webhook. There is no "Add funds" screen yet, so
 * the tenant integration starts the checkout; the payer's part happens in the browser.
 */
test('a hosted card payment settles the funding intent through the signed webhook', async ({ page, api, vendors }) => {
  const tenant = await api.makeTenant(await api.registerPerson('onramp'))
  await api.assignIdentomat(tenant)
  const subject = `payer-${Date.now().toString(36)}`
  const payerId = await api.enrolMember(tenant, subject)

  // Funding is a value action: the payer clears identity verification first.
  await vendors.interceptIdentomatWidget(page.context(), async (sessionToken) => {
    const session = await vendors.identomatSessionByToken(sessionToken)
    await vendors.setIdentomatVerdict(session.id, 'approved')
    await api.postIdentomatCallback(session.id, 'APPROVED')
  })
  const { verificationUrl } = await api.beginMemberKyc(tenant, subject)
  await page.goto(verificationUrl)
  await page.getByRole('button', { name: 'Complete verification' }).click()
  await expect.poll(() => api.memberKycStatus(tenant, subject), { timeout: 15_000 }).toBe('Approved')

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
  const settled = await api.checkoutIntent(tenant, payerId, checkout.intentId)
  expect(['payment_received', 'funds_applied']).toContain(settled.settlementState)
})
