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
