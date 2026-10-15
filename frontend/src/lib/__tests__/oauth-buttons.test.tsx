import { describe, expect, it, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'

/**
 * The register page previously shipped two `type="button"` elements with no
 * onClick — purely decorative. These tests are the regression guard: a rendered
 * provider button must actually begin the backend sign-in flow.
 */

const beginProviderSignIn = vi.fn()
const fetchEnabledProviders = vi.fn()

vi.mock('@/lib/oauth', async () => {
  const actual = await vi.importActual<typeof import('@/lib/oauth')>('@/lib/oauth')
  return {
    ...actual,
    beginProviderSignIn: (...args: unknown[]) => beginProviderSignIn(...args),
    fetchEnabledProviders: () => fetchEnabledProviders(),
  }
})

vi.mock('@/lib/azoa', () => ({
  azoa: { session: { token: null, restore: vi.fn() } },
}))

vi.mock('@/lib/runtime-config', () => ({
  readInitialApiUrl: () => 'https://api.example.test',
  resolveServerApiUrl: () => 'https://api.example.test',
}))

import { OAuthProviderButtons } from '@/lib/oauth-buttons'

describe('OAuthProviderButtons', () => {
  beforeEach(() => {
    beginProviderSignIn.mockClear()
    fetchEnabledProviders.mockReset()
  })

  it('renders nothing when the node has no providers configured', async () => {
    fetchEnabledProviders.mockResolvedValue([])

    render(<OAuthProviderButtons />)

    await waitFor(() => expect(fetchEnabledProviders).toHaveBeenCalled())
    expect(screen.queryByTestId('oauth-provider-buttons')).not.toBeInTheDocument()
  })

  it('renders only the providers the node advertises', async () => {
    fetchEnabledProviders.mockResolvedValue(['google'])

    render(<OAuthProviderButtons />)

    expect(await screen.findByRole('button', { name: /google/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /github/i })).not.toBeInTheDocument()
  })

  it('starts the backend sign-in flow when a provider button is pressed', async () => {
    fetchEnabledProviders.mockResolvedValue(['github', 'google'])
    const user = userEvent.setup()

    render(<OAuthProviderButtons returnTo="/overview" />)

    await user.click(await screen.findByRole('button', { name: /github/i }))

    expect(beginProviderSignIn).toHaveBeenCalledWith('github', {
      returnTo: '/overview',
      tenant: undefined,
    })
  })

  it('passes the tenant scope through so the binding lands in the right partition', async () => {
    fetchEnabledProviders.mockResolvedValue(['google'])
    const user = userEvent.setup()

    render(<OAuthProviderButtons tenant="11111111111111111111111111111111" />)

    await user.click(await screen.findByRole('button', { name: /google/i }))

    expect(beginProviderSignIn).toHaveBeenCalledWith('google', {
      returnTo: undefined,
      tenant: '11111111111111111111111111111111',
    })
  })

  it('disables every provider once one is pressed, so a double-click cannot start two flows', async () => {
    fetchEnabledProviders.mockResolvedValue(['github', 'google'])
    const user = userEvent.setup()

    render(<OAuthProviderButtons />)

    const github = await screen.findByRole('button', { name: /github/i })
    await user.click(github)

    expect(screen.getByRole('button', { name: /google/i })).toBeDisabled()
    expect(beginProviderSignIn).toHaveBeenCalledOnce()
  })
})
