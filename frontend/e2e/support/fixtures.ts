import { test as base, expect, type Page } from '@playwright/test'
import { AzoaApi, type Person } from './api'
import { loadE2eEnvironment, type E2eEnvironment } from './environment'
import { FakeVendors } from './vendors'

interface JourneyFixtures {
  environment: E2eEnvironment
  /** The API as operator, tenant integration and vendor callbacks reach it. */
  api: AzoaApi
  vendors: FakeVendors
}

/**
 * Playwright fixtures every journey uses. The provider callback is named `provide`, not `use`:
 * eslint-plugin-react-hooks treats any `use(...)` call as React's hook. See `e2e/AGENTS.md`.
 */
export const test = base.extend<JourneyFixtures>({
  // Playwright reads fixture dependencies from the destructuring pattern, so it must stay `{}`.
  // eslint-disable-next-line no-empty-pattern
  environment: async ({}, provide) => {
    await provide(loadE2eEnvironment())
  },
  api: async ({ playwright, environment }, provide) => {
    const request = await playwright.request.newContext({ baseURL: environment.apiUrl })
    await provide(new AzoaApi(request, environment))
    await request.dispose()
  },
  vendors: async ({ playwright, environment }, provide) => {
    const vendors = await FakeVendors.connect(playwright, environment)
    await provide(vendors)
    await vendors.dispose()
  },
})

export { expect }

/** Signs in through the real login page, as the person would. */
export async function signIn(page: Page, person: Person): Promise<void> {
  await page.goto('/login')
  await page.getByLabel('Email').fill(person.email)
  await page.getByLabel('Password').fill(person.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page).toHaveURL(/\/overview/)
}

/** Completes the tenant's authoritative hosted business review through the product UI. */
export async function completeBusinessVerification(page: Page, api: AzoaApi, vendors: FakeVendors): Promise<void> {
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
}
