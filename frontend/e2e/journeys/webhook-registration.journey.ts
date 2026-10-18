import { expect, signIn, test } from '../support/fixtures'

/** A tenant registers its webhook receiver in Settings; the signing secret is shown exactly once. */
test('a tenant registers a webhook destination and sees the secret only once', async ({ page, api }) => {
  const tenant = await api.makeTenant(await api.registerPerson('hooks'))
  await signIn(page, tenant)

  await page.goto('/settings')
  const panel = page.getByRole('region', { name: 'Webhook registration' })
  const save = panel.getByRole('button', { name: 'Save registration' })
  await expect(save).toBeEnabled()
  // Pinned in docker-compose.e2e.yml (extra_hosts) to the `webhook-receiver` service's public-range
  // address: no real DNS; delivery is on and lands at that receiver (e2e/AGENTS.md §webhook-receiver).
  await panel.getByLabel('HTTPS destination').fill('https://webhook-receiver.e2e.azoa.test/azoa')
  await panel.getByLabel('Registration change reason').fill('Register the journey test receiver.')
  await save.click()

  const secret = panel.getByLabel('New signing secret')
  await expect(secret).not.toHaveValue('')
  await expect(panel.getByText('Version 1; destination 1; signing secret 1.')).toBeVisible()

  await page.reload()
  await expect(page.getByRole('region', { name: 'Webhook registration' }).getByText('Version 1; destination 1; signing secret 1.')).toBeVisible()
  await expect(page.getByLabel('New signing secret')).toHaveCount(0)
})
