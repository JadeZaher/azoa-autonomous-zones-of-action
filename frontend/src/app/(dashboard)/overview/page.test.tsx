import { render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useChainInfo } from '@/lib/azoa-hooks'
import OverviewPage from './page'

vi.mock('@/lib/azoa-auth', () => ({
  useAzoaAuth: () => ({ userId: 'study-member', isAuthenticated: true, user: null }),
}))
vi.mock('@/lib/azoa-hooks', () => ({ useChainInfo: vi.fn() }))

describe('overview network status', () => {
  beforeEach(() => {
    vi.mocked(useChainInfo).mockReturnValue({
      info: null,
      loading: false,
      error: 'Chain info failed: TypeError: Failed to fetch',
    })
  })

  it('keeps each unavailable network identifiable without rendering raw transport errors', () => {
    render(<OverviewPage />)

    for (const chain of ['algorand', 'solana']) {
      const network = screen.getByRole('region', { name: `${chain} network status` })
      expect(within(network).getByRole('status')).toHaveTextContent('Network details could not be loaded. Try again later.')
    }
    expect(screen.getByText('Authenticated')).toBeInTheDocument()
    expect(screen.queryByText(/TypeError|Chain info failed|Failed to fetch/)).not.toBeInTheDocument()
  })

  it('renders returned network details when available', () => {
    vi.mocked(useChainInfo).mockReturnValue({ info: { blockHeight: 123 }, loading: false, error: null })
    render(<OverviewPage />)

    expect(within(screen.getByRole('region', { name: 'algorand network status' })).getByText('123')).toBeInTheDocument()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })
})
