import { expect, test } from '../support/fixtures'
import type { Tenant } from '../support/api'

/**
 * A person signs up in the UI, is onboarded as a tenant, sees its KYC policy, and one of its
 * members completes hosted identity verification. The product has no first-party KYC screen, so
 * the member's session is opened through the tenant API, exactly as an integration would.
 */
test('a new tenant\'s member opens the KYC gate only through the vendor\'s authoritative result', async ({ page, api, vendors }) => {
  const suffix = Date.now().toString(36)
  await page.goto('/register')
  await page.getByLabel('Username').fill(`journey${suffix}`)
  await page.getByLabel('Email').fill(`journey-${suffix}@e2e.azoa.test`)
  await page.getByLabel('Password').fill(`Journey-${suffix}-Pass1`)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page).toHaveURL(/\/overview/)

  const userId = await page.evaluate(() => localStorage.getItem('azoa_user_id'))
  expect(userId, 'the SDK persisted the signed-in user').toBeTruthy()
  const tenant: Tenant = await api.makeTenant({
    userId: userId!,
    username: `journey${suffix}`,
    email: `journey-${suffix}@e2e.azoa.test`,
    password: `Journey-${suffix}-Pass1`,
  })
  await api.assignIdentomat(tenant)

  await page.goto('/kyc')
  await expect(page.getByText('Current tenant policy')).toBeVisible()
  await expect(page.getByText('Identomat', { exact: true }).first()).toBeVisible()

  const subject = `member-${suffix}`
  await api.enrolMember(tenant, subject)
  const { verificationUrl } = await api.beginMemberKyc(tenant, subject)
  expect(await api.memberKycStatus(tenant, subject)).toBe('Pending')

  await vendors.interceptIdentomatWidget(page.context(), async (sessionToken) => {
    const session = await vendors.identomatSessionByToken(sessionToken)
    await vendors.setIdentomatVerdict(session.id, 'approved')
    await api.postIdentomatCallback(session.id, 'APPROVED')
  })
  const hosted = await page.context().newPage()
  await hosted.goto(verificationUrl)
  await hosted.getByRole('button', { name: 'Complete verification' }).click()
  await expect(hosted.getByRole('status')).toHaveText('Verification submitted')

  await expect.poll(() => api.memberKycStatus(tenant, subject), { timeout: 15_000 }).toBe('Approved')
})
