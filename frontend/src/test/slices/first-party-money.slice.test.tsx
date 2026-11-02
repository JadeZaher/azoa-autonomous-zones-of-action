import type { ContextType, ReactElement } from 'react'
import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import CashOutPage from '@/app/(dashboard)/cash-out/page'
import { MemberMoneySurface } from '@/components/money/member-money'
import { AzoaContext } from '@/lib/azoa-context'
import { cashOutApi, checkoutApi, firstPartyMoneyAccountApi } from '../msw/handlers/fiat'
import { SLICE_TENANT_ID, renderSlice } from './render-slice'

/** Only the React session context is supplied by hand; the SDK and its HTTP calls are real (MSW-faked API). */
function signedIn(ui: ReactElement) {
  const session = { userId: SLICE_TENANT_ID, isAuthenticated: true, authLoading: false } as unknown as ContextType<typeof AzoaContext>
  return <AzoaContext.Provider value={session}>{ui}</AzoaContext.Provider>
}

/** First-party money pages: the saved request key must never let one intent turn into two payouts. See `src/app/(dashboard)/AGENTS.md` §first-party-money. */
describe('first-party money slice', () => {
  it('cash-out: a lost answer blocks another amount and resumes the same key instead of claiming a second payout', async () => {
    const api = cashOutApi()
    const { user } = await renderSlice(signedIn(<CashOutPage />), { apis: [api, firstPartyMoneyAccountApi(SLICE_TENANT_ID)] })
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

    expect(screen.queryByLabelText('Amount')).not.toBeInTheDocument()
    expect(api.claims).toHaveLength(1)

    const resume = screen.getByRole('button', { name: 'Resume that cash-out' })
    await waitFor(() => expect(resume).toBeEnabled())
    await user.click(resume)
    expect(await screen.findByText('Your cash-out is on its way.', { selector: '[role="status"]' })).toBeInTheDocument()
    expect(screen.queryByText(/has not been confirmed yet/)).not.toBeInTheDocument()
    expect(api.claims).toHaveLength(1)
    expect(api.attemptedRequestKeys).toHaveLength(2)
    expect(new Set(api.attemptedRequestKeys).size).toBe(1)
  })

  it('cash-out: a refusal retains the key until server void proof permits a new amount', async () => {
    const api = cashOutApi()
    const { user } = await renderSlice(signedIn(<CashOutPage />), { apis: [api, firstPartyMoneyAccountApi(SLICE_TENANT_ID)] })
    await screen.findByText('Cashable now')
    const amount = screen.getByLabelText('Amount')
    const cashOut = screen.getByRole('button', { name: 'Cash out' })
    await waitFor(() => expect(cashOut).toBeEnabled())

    api.refuseNext(400, 'Verify your identity before cashing out.')
    await user.type(amount, '30.00')
    await user.click(cashOut)
    expect(await screen.findByText(/A cash-out of 30.00 USD has not been confirmed yet/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Amount')).not.toBeInTheDocument()
    expect(api.claims).toHaveLength(0)
    expect(api.voidCalls).toHaveLength(0)

    const cancel = screen.getByRole('button', { name: 'Check or cancel request' })
    await waitFor(() => expect(cancel).toBeEnabled())
    await user.click(cancel)
    expect(await screen.findByText(/cancelled before admission/)).toBeInTheDocument()
    expect(api.voidCalls).toEqual([api.attemptedRequestKeys[0]])
    const nextAmount = await screen.findByLabelText('Amount')
    await user.clear(nextAmount)
    await user.type(nextAmount, '20.00')
    await user.click(screen.getByRole('button', { name: 'Cash out' }))
    expect(await screen.findByText('Your cash-out is on its way.', { selector: '[role="status"]' })).toBeInTheDocument()
    expect(screen.queryByText(/has not been confirmed yet/)).not.toBeInTheDocument()
    expect(api.claims.map(claim => claim.amountMinorUnits)).toEqual([20_00])
    expect(api.attemptedRequestKeys).toHaveLength(2)
    expect(api.attemptedRequestKeys[1]).not.toBe(api.attemptedRequestKeys[0])
  })

  it('cash-out: cancellation after a lost answer reveals the admitted payout without creating or cancelling another', async () => {
    const api = cashOutApi()
    const { user } = await renderSlice(signedIn(<CashOutPage />), { apis: [api, firstPartyMoneyAccountApi(SLICE_TENANT_ID)] })
    await screen.findByText('Cashable now')
    api.loseNextResponse()
    await user.type(screen.getByLabelText('Amount'), '50.00')
    await user.click(screen.getByRole('button', { name: 'Cash out' }))
    const cancel = await screen.findByRole('button', { name: 'Check or cancel request' })
    await waitFor(() => expect(cancel).toBeEnabled())
    await user.click(cancel)
    expect(await screen.findByText('Your cash-out is on its way.', { selector: '[role="status"]' })).toBeInTheDocument()
    expect(api.voidCalls).toEqual([api.attemptedRequestKeys[0]])
    expect(api.claims).toHaveLength(1)
    expect(api.claims[0].status).toBe('Requested')
    expect(api.attemptedRequestKeys).toHaveLength(1)
  })

  it('funds: a payment settled while away is resolved on return, and the same amount later opens a new payment', async () => {
    const api = checkoutApi()
    const onRedirect = vi.fn()
    const first = await renderSlice(signedIn(<MemberMoneySurface kind="funds" onRedirect={onRedirect} />),
      { apis: [api, cashOutApi(), firstPartyMoneyAccountApi(SLICE_TENANT_ID)] })
    const pay = await screen.findByRole('button', { name: 'Continue to payment' })
    await waitFor(() => expect(pay).toBeEnabled())
    await first.user.type(screen.getByLabelText('Amount'), '25')
    await first.user.click(pay)
    expect(await screen.findByRole('button', { name: /payment page/i })).toBeInTheDocument()
    expect(onRedirect).toHaveBeenLastCalledWith('https://checkout.stripe.com/pay/slice')
    expect(api.successUrls[0]).toMatch(/\/funds$/)

    // Stripe settles; the person comes back later without an `?intent` on the URL.
    api.settle(api.intents[0].intentId)
    first.unmount()
    const later = await renderSlice(signedIn(<MemberMoneySurface kind="funds" onRedirect={onRedirect} />), { apis: [] })
    expect(await screen.findByText('The funds are in your account.')).toBeInTheDocument()

    const payAgain = screen.getByRole('button', { name: 'Continue to payment' })
    await waitFor(() => expect(payAgain).toBeEnabled())
    await later.user.type(screen.getByLabelText('Amount'), '25')
    await later.user.click(payAgain)
    expect(await screen.findByRole('button', { name: /payment page/i })).toBeInTheDocument()
    expect(screen.queryByText(/\(resumed\)/)).not.toBeInTheDocument()
    expect(api.intents).toHaveLength(2)
    expect(onRedirect).toHaveBeenCalledTimes(2)
  })
})
