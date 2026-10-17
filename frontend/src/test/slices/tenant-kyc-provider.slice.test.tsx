import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import TenantKycPage from '@/app/(dashboard)/kyc/page'
import { tenantKycApi } from '../msw/handlers'
import { tenantKycFixtures } from '../msw/fixtures'
import { renderSlice } from './render-slice'

const otherProvider = {
  providerKey: 'hosted',
  displayName: 'Partner hosted identity',
  assuranceLevel: 'document-and-liveness',
  hostedVerification: true,
  acceptsDocumentReferences: false,
}

/** Tenant KYC provider policy page against the real SDK; see `src/test/AGENTS.md` §slices. */
describe('tenant KYC provider slice', () => {
  it('shows the current policy and no choices when the operator has not admitted an override', async () => {
    const api = tenantKycApi({ choices: [], selection: tenantKycFixtures.selectedIdentomat })
    await renderSlice(<TenantKycPage />, { apis: [api] })

    expect(await screen.findByText('Selection revision 1')).toBeInTheDocument()
    expect(screen.getByText('Identomat')).toBeInTheDocument()
    expect(screen.getByText('READY')).toBeInTheDocument()
    expect(screen.getByText(/No provider is ready for tenant selection/)).toBeInTheDocument()
  })

  it('a provider change needs explicit confirmation and a fresh sign-in when the session is stale', async () => {
    const api = tenantKycApi({
      choices: [tenantKycFixtures.identomatChoice, otherProvider],
      selection: tenantKycFixtures.selectedIdentomat,
    })
    const { user } = await renderSlice(<TenantKycPage />, { apis: [api] })

    await user.click(await screen.findByRole('radio', { name: /Partner hosted identity/ }))
    const confirm = screen.getByRole('button', { name: 'Confirm provider change' })
    expect(confirm).toBeDisabled()
    await user.click(screen.getByRole('checkbox', { name: /makes active attempts and prior approvals stale/ }))
    await waitFor(() => expect(confirm).toBeEnabled())

    api.requireRecentLogin()
    await user.click(confirm)
    expect(await screen.findByRole('link', { name: 'Sign in again' })).toHaveAttribute(
      'href',
      '/login?reason=step-up&returnTo=%2Fkyc',
    )
    expect(api.selection.providerKey).toBe('identomat')

    // The confirmation survives the refusal; signing in again and retrying applies the change.
    await user.click(screen.getByRole('button', { name: 'Confirm provider change' }))
    expect(await screen.findByText('KYC provider changed to Partner hosted identity.')).toBeInTheDocument()
    expect(api.selection.selectionVersion).toBe(2)
  })
})
