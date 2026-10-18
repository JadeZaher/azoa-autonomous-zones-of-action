import type { ContextType, ReactElement } from 'react'
import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import CashOutPage from '@/app/(dashboard)/cash-out/page'
import FundsPage from '@/app/(dashboard)/funds/page'
import { AzoaContext } from '@/lib/azoa-context'
import { cashOutApi, checkoutApi } from '../msw/handlers/fiat'
import { SLICE_TENANT_ID, renderSlice } from './render-slice'

/** Only the React session context is supplied by hand; the SDK and its HTTP calls are real (MSW-faked API). */
function signedIn(ui: ReactElement) {
  const session = { userId: SLICE_TENANT_ID, isAuthenticated: true, authLoading: false } as unknown as ContextType<typeof AzoaContext>
  return <AzoaContext.Provider value={session}>{ui}</AzoaContext.Provider>
}

/** First-party money pages: the saved request key must never let one intent turn into two payouts. See `src/app/(dashboard)/AGENTS.md` §first-party-money. */
describe('first-party money slice', () => {
  it('cash-out: a lost answer shows the claim, and an edited amount resumes it instead of claiming a second payout', async () => {
    const api = cashOutApi()
    const { user } = await renderSlice(signedIn(<CashOutPage />), { apis: [api] })
    await screen.findByText('Cashable now')
    const amount = screen.getByLabelText('Amount')
    const cashOut = screen.getByRole('button', { name: 'Cash out' })
    await waitFor(() => expect(cashOut).toBeEnabled())

    api.loseNextResponse()
    await user.type(amount, '50.00')
    await user.click(cashOut)
    expect(await screen.findByText(/has not been confirmed yet/)).toBeInTheDocument()
    // The history is re-read after the failure, so the claim the node did make is visible.
    expect(await screen.findByRole('listitem')).toHaveTextContent('50.00 USD')

    await user.clear(amount)
    await user.type(amount, '49.00')
    await user.click(cashOut)
    expect(screen.getByRole('alert')).toHaveTextContent('A cash-out of 50.00 USD has not been confirmed yet.')
    expect(api.claims).toHaveLength(1)

    await user.click(screen.getByRole('button', { name: 'Resume that cash-out' }))
    expect(await screen.findByText('Your cash-out is on its way.', { selector: '[role="status"]' })).toBeInTheDocument()
    expect(screen.queryByText(/has not been confirmed yet/)).not.toBeInTheDocument()
    expect(api.claims).toHaveLength(1)
  })

  it('cash-out: a definite refusal releases the key, so a new amount starts a new request', async () => {
    const api = cashOutApi()
    const { user } = await renderSlice(signedIn(<CashOutPage />), { apis: [api] })
    await screen.findByText('Cashable now')
    const amount = screen.getByLabelText('Amount')
    const cashOut = screen.getByRole('button', { name: 'Cash out' })
    await waitFor(() => expect(cashOut).toBeEnabled())

    api.refuseNext(400, 'Verify your identity before cashing out.')
    await user.type(amount, '30.00')
    await user.click(cashOut)
    expect(await screen.findByText('Verify your identity before cashing out.')).toBeInTheDocument()

    await user.clear(amount)
    await user.type(amount, '20.00')
    await user.click(cashOut)
    expect(await screen.findByText('Your cash-out is on its way.', { selector: '[role="status"]' })).toBeInTheDocument()
    expect(screen.queryByText(/has not been confirmed yet/)).not.toBeInTheDocument()
    expect(api.claims.map(claim => claim.amountMinorUnits)).toEqual([20_00])
  })

  it('funds: a payment settled while away is resolved on return, and the same amount later opens a new payment', async () => {
    const api = checkoutApi()
    const first = await renderSlice(signedIn(<FundsPage />), { apis: [api] })
    const pay = await screen.findByRole('button', { name: 'Continue to payment' })
    await waitFor(() => expect(pay).toBeEnabled())
    await first.user.type(screen.getByLabelText('Amount'), '25')
    await first.user.click(pay)
    expect(await screen.findByRole('link', { name: /Open the payment page/ })).toBeInTheDocument()
    expect(api.successUrls[0]).toMatch(/\/funds$/)

    // Stripe settles; the person comes back later without an `?intent` on the URL.
    api.settle(api.intents[0].intentId)
    first.unmount()
    const later = await renderSlice(signedIn(<FundsPage />), { apis: [] })
    expect(await screen.findByText('The funds are in your account.')).toBeInTheDocument()

    const payAgain = screen.getByRole('button', { name: 'Continue to payment' })
    await waitFor(() => expect(payAgain).toBeEnabled())
    await later.user.type(screen.getByLabelText('Amount'), '25')
    await later.user.click(payAgain)
    expect(await screen.findByRole('link', { name: /Open the payment page/ })).toBeInTheDocument()
    expect(screen.queryByText(/\(resumed\)/)).not.toBeInTheDocument()
    expect(api.intents).toHaveLength(2)
  })
})
