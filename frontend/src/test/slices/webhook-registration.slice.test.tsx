import { screen, waitFor } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { WebhookRegistrationPanel } from '@/components/webhooks/webhook-registration'
import { webhookRegistrationApi } from '../msw/handlers'
import { renderSlice, SLICE_TENANT_ID } from './render-slice'

/** Tenant webhook registration against the real SDK; see `src/test/AGENTS.md` §slices. */
describe('webhook registration slice', () => {
  async function fillAndSave(user: Awaited<ReturnType<typeof renderSlice>>['user']) {
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save registration' })).toBeEnabled())
    await user.type(screen.getByLabelText('HTTPS destination'), 'https://receiver.example/azoa')
    await user.type(screen.getByLabelText('Registration change reason'), 'Configure our event receiver.')
    await user.click(screen.getByRole('button', { name: 'Save registration' }))
  }

  it('registers a destination and shows the signing secret once without persisting it', async () => {
    const api = webhookRegistrationApi({ tenantId: SLICE_TENANT_ID })
    const { user } = await renderSlice(<WebhookRegistrationPanel userId={SLICE_TENANT_ID} />, { apis: [api] })

    await fillAndSave(user)

    const secret = (await screen.findByLabelText('New signing secret')) as HTMLInputElement
    expect(secret.value).toMatch(/^whsig-/)
    expect(screen.getByText('Version 1; destination 1; signing secret 1.')).toBeInTheDocument()
    expect(api.current).toMatchObject({ version: 1, url: 'https://receiver.example/azoa', isActive: true })
    expect(JSON.stringify({ ...localStorage })).not.toContain(secret.value)
  })

  it('retries a lost response with the identical command and honors the server replay', async () => {
    const api = webhookRegistrationApi({ tenantId: SLICE_TENANT_ID })
    api.loseNextResponse()
    const { user } = await renderSlice(<WebhookRegistrationPanel userId={SLICE_TENANT_ID} />, { apis: [api] })

    await fillAndSave(user)
    const retry = await screen.findByRole('button', { name: 'Retry saved change' })
    expect(localStorage.getItem(`azoa:webhook-registration:${SLICE_TENANT_ID}`)).not.toBeNull()

    await user.click(retry)

    // The API replays only the identical command under the same request id, so this message proves
    // the retry resent the saved command rather than minting a new change.
    expect(await screen.findByText(/The original change was accepted/)).toBeInTheDocument()
    expect(screen.queryByLabelText('New signing secret')).not.toBeInTheDocument()
    expect(localStorage.getItem(`azoa:webhook-registration:${SLICE_TENANT_ID}`)).toBeNull()
    expect(api.current?.version).toBe(1)
  })
})
