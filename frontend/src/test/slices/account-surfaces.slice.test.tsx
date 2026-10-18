import { screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import LoginPage from '@/app/(auth)/login/page'
import ApiKeysPage from '@/app/(dashboard)/api-keys/page'
import UsersPage from '@/app/(dashboard)/users/page'
import { FungibleCreationPanel } from '@/components/fungible/fungible-creation-panel'
import { apiKeysApi, fungibleCreationApi, loginApi, profileUpdateApi, type ApiKeyWireRow } from '../msw/handlers/account'
import { renderSlice, renderSliceWithAuth, SLICE_TENANT_ID } from './render-slice'

const router = vi.hoisted(() => ({ replace: vi.fn(), push: vi.fn(), refresh: vi.fn(), back: vi.fn(), prefetch: vi.fn() }))
vi.mock('next/navigation', () => ({ useRouter: () => router, usePathname: () => '/', useSearchParams: () => new URLSearchParams() }))

const key = (overrides: Partial<ApiKeyWireRow>): ApiKeyWireRow => ({
  id: '0f0e0d0c-0b0a-4908-8706-050403020100', name: 'ci', keyPrefix: 'azoa_83dd1631b5f', createdDate: '2026-10-18T05:00:00Z',
  expiresAt: null, lastUsedAt: null, revokedAt: null, isActive: true, scopes: 'wallet:manage,nft:mint', allowedOrigins: null, ...overrides,
})

/** Account surfaces that failed live QA on 2026-10-18 (`round4-post-qa-deferrals.md` H1-H4), driven through the real SDK. */
describe('account surfaces after live QA', () => {
  it('a wrong password shows the server refusal, not a network failure (H1)', async () => {
    const { user } = await renderSliceWithAuth(<LoginPage />, {
      apis: [loginApi({ email: 'qa@example.test', password: 'right-password', token: 'unused' })],
      session: 'anonymous',
    })

    await user.type(screen.getByLabelText('Email'), 'qa@example.test')
    await user.type(screen.getByLabelText('Password'), 'wrong-password')
    await user.click(screen.getByRole('button', { name: /Sign in/ }))

    expect(await screen.findByText('Invalid credentials.')).toBeInTheDocument()
    expect(screen.queryByText(/network request failed/)).not.toBeInTheDocument()
    expect(router.replace).not.toHaveBeenCalled()
  })

  it('lists keys whose scopes arrive as a comma-separated string, and revokes one (H2)', async () => {
    const api = apiKeysApi([key({}), key({ id: '1f0e0d0c-0b0a-4908-8706-050403020100', name: 'legacy', scopes: null })])
    const { user } = await renderSlice(<ApiKeysPage />, { apis: [api] })

    const ciRow = (await screen.findByText('ci')).closest('tr')!
    expect(within(ciRow).getByText('wallet:manage')).toBeInTheDocument()
    expect(within(ciRow).getByText('nft:mint')).toBeInTheDocument()
    expect(within(ciRow).getByText('azoa_83dd1631b5f...')).toBeInTheDocument()
    expect(within(screen.getByText('legacy').closest('tr')!).getByText('full access')).toBeInTheDocument()

    await user.click(within(ciRow).getByRole('button', { name: 'Revoke' }))
    await user.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Revoke' }))

    await waitFor(() => expect(within(screen.getByText('ci').closest('tr')!).getByText('revoked')).toBeInTheDocument())
    expect(api.rows.find(row => row.name === 'ci')?.isActive).toBe(false)
  })

  it('shows creations the controller returns as a bare array (H3)', async () => {
    const view = { operationId: 'saved-operation', version: 2, state: 'AwaitingConsent', chainType: 'Algorand', network: 'Devnet', purpose: 'Participant', walletAddress: 'reserve', walletId: 'wallet', walletProvisioned: false }
    await renderSlice(<FungibleCreationPanel actorId={SLICE_TENANT_ID} />, { apis: [fungibleCreationApi([view])] })

    expect(await screen.findByText(/AwaitingConsent - Algorand/)).toBeInTheDocument()
  })

  it('saves only edited fields and shows the server validation message (H4)', async () => {
    const api = profileUpdateApi(SLICE_TENANT_ID)
    const { user } = await renderSliceWithAuth(<UsersPage />, { apis: [api] })

    const username = await screen.findByLabelText('Username')
    await waitFor(() => expect(username).toHaveValue('slice-owner'))
    await user.type(screen.getByLabelText('Title'), 'Steward')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText('Profile updated.')).toBeInTheDocument()
    expect(api.received[0]).toEqual({ title: 'Steward' })

    await user.clear(username)
    await user.type(username, 'bad name!')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText(/contain only letters, numbers, underscores, or hyphens/)).toBeInTheDocument()
    expect(screen.queryByText(/failed with HTTP 400/)).not.toBeInTheDocument()

    await user.clear(username)
    await user.type(username, 'wallet-mine')
    await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByText(/reserved for generated accounts/)).toBeInTheDocument()
  })
})
