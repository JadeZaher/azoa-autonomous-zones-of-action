import { expect, test } from '@playwright/test'

/** The app shell serves the sign-in page; runs without the API (`--project shell`). */
test('the sign-in page renders its form', async ({ page }) => {
  await page.goto('/login')
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible()
  await expect(page.getByLabel('Email')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
})
