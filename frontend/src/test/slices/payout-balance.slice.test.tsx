import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { TenantPayoutFloatView } from 'azoa-sdk'
import PayoutBalancePage from '@/app/(dashboard)/payout-balance/page'
import { TENANT_PAYOUT_SLICE_PAYMENT_URL, tenantPayoutApi } from '../msw/handlers/tenant-payout'
import { renderSliceWithAuth, SLICE_TENANT_ID } from './render-slice'

const charged: TenantPayoutFloatView = {
  floatId: 'float-1', tenantId: SLICE_TENANT_ID, currencyCode: 'usd', environment: 'test',
  creditedMinorUnits: 50000, reservedMinorUnits: 0, finalizedMinorUnits: 0, availableMinorUnits: 0,
  deficitMinorUnits: 12500, pendingCreditMinorUnits: 0, recomputedCreditedMinorUnits: 50000,
  recomputedReservedMinorUnits: 0, recomputedFinalizedMinorUnits: 0, consistent: true,
}

/** Tenant payout balance (D-8) inside the real auth provider; see `src/test/AGENTS.md` §slices. */
describe('payout balance slice', () => {
  it('starts one top-up and resumes the same request when the owner continues again', async () => {
    const api = tenantPayoutApi()
    const { user } = await renderSliceWithAuth(<PayoutBalancePage />, { apis: [api] })

    expect(await screen.findByText(/No balance yet/)).toBeInTheDocument()
    await user.type(screen.getByLabelText('Amount'), '500.00')
    const pay = screen.getByRole('button', { name: 'Continue to payment' })
    await waitFor(() => expect(pay).toBeEnabled())
    await user.click(pay)
    expect(await screen.findByRole('link', { name: /Open the payment page/ })).toHaveAttribute('href', TENANT_PAYOUT_SLICE_PAYMENT_URL)

    await user.click(pay)
    await waitFor(() => expect(pay).toBeEnabled())
    expect(api.topUps.size).toBe(1)
    expect([...api.topUps.values()][0].amountMinorUnits).toBe(50000)
  })

  it('warns that tenant-priced cash-outs are refused while a charged-back top-up is uncovered', async () => {
    const api = tenantPayoutApi([charged])
    await renderSliceWithAuth(<PayoutBalancePage />, { apis: [api] })

    expect(await screen.findByRole('alert')).toHaveTextContent(/every cash-out priced by you is refused until it is topped up/)
  })
})
