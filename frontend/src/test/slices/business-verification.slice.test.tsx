import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import BusinessVerificationPage from '@/app/(dashboard)/business-verification/page'
import { businessVerificationApi } from '../msw/handlers'
import { businessVerificationFixtures } from '../msw/fixtures'
import { renderSlice } from './render-slice'

/** Tenant business verification (KYB) page against the real SDK; see `src/test/AGENTS.md` §slices. */
describe('business verification slice', () => {
  it('starts a review, links to the hosted flow, and shows the approval after a refresh', async () => {
    const api = businessVerificationApi()
    const { user } = await renderSlice(<BusinessVerificationPage />, { apis: [api] })

    expect(await screen.findByText('No verification started.')).toBeInTheDocument()
    const start = screen.getByRole('button', { name: 'Start verification' })
    await waitFor(() => expect(start).toBeEnabled())
    await user.click(start)

    expect(await screen.findByText('Status: pending')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Continue with Identomat' })).toHaveAttribute(
      'href',
      businessVerificationFixtures.sessionStarted.verificationUrl,
    )
    expect(api.status?.status).toBe('PENDING')

    api.decide('APPROVED')
    await user.click(screen.getByRole('button', { name: 'Refresh status' }))
    expect(await screen.findByText('Business verified')).toBeInTheDocument()
    expect(screen.getByText(/Linked participants: 1/)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Continue with Identomat' })).not.toBeInTheDocument()
  })

  it('resumes a pending review instead of opening a second one', async () => {
    const api = businessVerificationApi({ initial: businessVerificationFixtures.pending })
    const { user } = await renderSlice(<BusinessVerificationPage />, { apis: [api] })

    const resume = await screen.findByRole('button', { name: 'Resume verification' })
    await waitFor(() => expect(resume).toBeEnabled())
    await user.click(resume)

    expect(await screen.findByRole('link', { name: 'Continue with Identomat' })).toBeInTheDocument()
    expect(api.status?.attemptId).toBe(businessVerificationFixtures.pending.attemptId)
  })

  it('tells the tenant the node has not enabled business verification', async () => {
    const api = businessVerificationApi({ available: false })
    await renderSlice(<BusinessVerificationPage />, { apis: [api] })

    expect(await screen.findByText(/Business verification is currently unavailable/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Start verification' })).toBeDisabled()
  })

  it('asks a signed-out browser to sign in as a tenant administrator', async () => {
    const api = businessVerificationApi()
    await renderSlice(<BusinessVerificationPage />, { apis: [api], session: 'anonymous' })

    expect(await screen.findByText(/Sign in as an active tenant administrator/)).toBeInTheDocument()
  })
})
