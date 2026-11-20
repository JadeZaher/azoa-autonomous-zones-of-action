import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import OverviewPage from '../page'

const writeText = vi.fn(async () => {})

vi.mock('@/lib/azoa-auth', () => ({
  useAzoaAuth: () => ({
    userId: 'user-abc-123',
    isAuthenticated: true,
    user: { id: 'user-abc-123', username: 'ada', email: 'ada@azoa.example', dappRole: 'dapp:user' },
    loading: false,
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  }),
}))

vi.mock('@/lib/azoa-hooks', () => ({
  useChainInfo: (chain: string) => ({
    info: { network: chain === 'algorand' ? 'testnet' : 'devnet', height: 42 },
    loading: false,
    error: null,
  }),
}))

describe('OverviewPage first-mile launchpad', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('renders First-Mile Quickstart cards with action links, role badge, copyable user ID, and chain cards', async () => {
    const user = userEvent.setup()
    vi.spyOn(navigator.clipboard, 'writeText').mockImplementation(writeText)

    render(<OverviewPage />)

    const quickstart = screen.getByRole('region', { name: 'First-mile quickstart' })
    expect(quickstart).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Verify identity/ })).toHaveAttribute('href', '/verify-identity')
    expect(screen.getByRole('link', { name: /Documents & invoices/ })).toHaveAttribute('href', '/documents')
    expect(screen.getByRole('link', { name: /Open workflows/ })).toHaveAttribute('href', '/actions')

    expect(screen.getByText('dapp:user')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Role assignment' })).toHaveAttribute('href', '/admin/roles')

    await user.click(screen.getByRole('button', { name: 'Copy user ID' }))
    expect(writeText).toHaveBeenCalledWith('user-abc-123')
    expect(await screen.findByText('Copied')).toBeInTheDocument()

    expect(screen.getByRole('region', { name: 'algorand network status' })).toBeInTheDocument()
    expect(screen.getByRole('region', { name: 'solana network status' })).toBeInTheDocument()
  })
})
