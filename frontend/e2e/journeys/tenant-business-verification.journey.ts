import { expect, signIn, test } from '../support/fixtures'

/** A tenant administrator verifies its organization (KYB) through the hosted Identomat flow. */
test('a tenant verifies its business through the hosted flow and sees the approval', async ({ page, api, vendors }) => {
  const tenant = await api.makeTenant(await api.registerPerson('kyb'))
  await signIn(page, tenant)

  await page.goto('/business-verification')
  await page.getByRole('button', { name: 'Start verification' }).click()
  await expect(page.getByText('Status: pending')).toBeVisible()

  await vendors.interceptIdentomatWidget(page.context(), async (sessionToken) => {
    const session = await vendors.identomatSessionByToken(sessionToken)
    await vendors.setIdentomatVerdict(session.id, 'approved')
    await api.postIdentomatCallback(session.id, 'approved')
  })
  const popupOpened = page.waitForEvent('popup')
  await page.getByRole('link', { name: 'Continue with Identomat' }).click()
  const hosted = await popupOpened
  await hosted.getByRole('button', { name: 'Complete verification' }).click()
  await expect(hosted.getByRole('status')).toHaveText('Verification submitted')
  await hosted.close()

  await page.getByRole('button', { name: 'Refresh status' }).click()
  await expect(page.getByText('Business verified')).toBeVisible()
  await expect(page.getByText(/Linked participants: 1/)).toBeVisible()
})
