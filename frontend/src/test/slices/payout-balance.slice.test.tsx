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
  strandedReservedCount: 0, oldestReservedAt: null,
}

const stranded: TenantPayoutFloatView = {
  floatId: 'float-2', tenantId: SLICE_TENANT_ID, currencyCode: 'usd', environment: 'test',
  creditedMinorUnits: 50000, reservedMinorUnits: 20000, finalizedMinorUnits: 0, availableMinorUnits: 30000,
  deficitMinorUnits: 0, pendingCreditMinorUnits: 0, recomputedCreditedMinorUnits: 50000,
  recomputedReservedMinorUnits: 20000, recomputedFinalizedMinorUnits: 0, consistent: false,
  strandedReservedCount: 2, oldestReservedAt: '2026-10-01T00:00:00Z',
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

  it('surfaces stranded reservations once the float disagrees with its own ledger (M4)', async () => {
    const api = tenantPayoutApi([stranded])
    await renderSliceWithAuth(<PayoutBalancePage />, { apis: [api] })

    expect(await screen.findByRole('alert')).toHaveTextContent('2 reservations for a cash-out')
    expect(screen.getByRole('alert')).toHaveTextContent(/held since \d+\/\d+\/2026/)
  })

  it('says payments are not configured when the node has no payment rail, and keeps no resumable top-up (M3)', async () => {
    const api = tenantPayoutApi([], { railUnavailable: true })
    const { user } = await renderSliceWithAuth(<PayoutBalancePage />, { apis: [api] })

    await user.type(await screen.findByLabelText('Amount'), '25.00')
    const pay = screen.getByRole('button', { name: 'Continue to payment' })
    await waitFor(() => expect(pay).toBeEnabled())
    await user.click(pay)

    expect(await screen.findByText(/Payments are not configured on this node/)).toBeInTheDocument()
    expect(screen.queryByText(/Try again shortly/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Resume that top-up/ })).not.toBeInTheDocument()
    expect(pay).toBeDisabled()
    expect(api.topUps.size).toBe(0)
  })
})
