import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import VerifyIdentityPage from '@/app/(dashboard)/verify-identity/page'
import { KYC_SLICE_VERIFICATION_URL, kycApi } from '../msw/handlers/kyc'
import { renderSlice } from './render-slice'

/** First-party identity verification against the real SDK; see `src/test/AGENTS.md` §slices. */
describe('verify identity slice', () => {
  it('starts a hosted session, links to the provider, and shows the decision only after a refresh', async () => {
    const api = kycApi()
    const { user } = await renderSlice(<VerifyIdentityPage />, { apis: [api] })

    expect(await screen.findByText('You have not started a verification yet.')).toBeInTheDocument()
    const start = screen.getByRole('button', { name: 'Start verification' })
    await waitFor(() => expect(start).toBeEnabled())
    await user.click(start)

    expect(await screen.findByRole('link', { name: /Continue with the verification provider/ })).toHaveAttribute('href', KYC_SLICE_VERIFICATION_URL)
    expect(api.sessionStarts).toBe(1)

    api.decide('APPROVED')
    await user.click(screen.getByRole('button', { name: 'Refresh result' }))
    expect(await screen.findByText('Identity verified')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Start verification|Resume verification/ })).not.toBeInTheDocument()
  })

  it('reports a rejection with its reason instead of a success', async () => {
    const api = kycApi()
    const { user } = await renderSlice(<VerifyIdentityPage />, { apis: [api] })
    const start = await screen.findByRole('button', { name: 'Start verification' })
    await waitFor(() => expect(start).toBeEnabled())
    await user.click(start)
    await screen.findByRole('link', { name: /Continue with the verification provider/ })

    api.decide('REJECTED')
    await user.click(screen.getByRole('button', { name: 'Refresh result' }))
    expect(await screen.findByText(/could not verify you/)).toBeInTheDocument()
    expect(screen.getByText('Reason given: Document unreadable')).toBeInTheDocument()
  })
})
