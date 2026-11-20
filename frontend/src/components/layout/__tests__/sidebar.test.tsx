import { cleanup, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MobileSidebar, Sidebar } from '../sidebar'

vi.mock('next/navigation', () => ({ usePathname: () => '/overview' }))

afterEach(() => cleanup())

describe('mobile account navigation', () => {
  it('opens the named navigation dialog from an accessible menu button', async () => {
    const user = userEvent.setup()
    render(<MobileSidebar />)

    await user.click(screen.getByRole('button', { name: 'Open navigation' }))

    const navigation = await screen.findByRole('dialog', { name: 'Navigation' })
    expect(within(navigation).getByRole('link', { name: 'Wallets' })).toBeInTheDocument()
    expect(within(navigation).getByRole('link', { name: 'Settings' })).toBeInTheDocument()
  })
})

describe('desktop sidebar decluttering', () => {
  it('prioritizes core workflow links and places developer plumbing under Builder & Advanced', async () => {
    const user = userEvent.setup()
    render(<Sidebar />)

    expect(screen.getByRole('link', { name: 'Overview' })).toHaveAttribute('href', '/overview')
    expect(screen.getByRole('link', { name: 'Verify identity' })).toHaveAttribute('href', '/verify-identity')
    expect(screen.getByRole('link', { name: 'Documents & invoices' })).toHaveAttribute('href', '/documents')
    expect(screen.getByRole('link', { name: 'Actions' })).toHaveAttribute('href', '/actions')
    expect(screen.getByRole('link', { name: 'Wallets' })).toHaveAttribute('href', '/wallets')
    expect(screen.getByRole('link', { name: 'Settings' })).toHaveAttribute('href', '/settings')

    const advancedSummary = screen.getByText('Builder & Advanced')
    expect(advancedSummary).toBeInTheDocument()
    await user.click(advancedSummary)
    expect(screen.getByRole('link', { name: 'Action Graph Bundles' })).toHaveAttribute('href', '/action-graph-bundle')
    expect(screen.getByRole('link', { name: 'Holons' })).toHaveAttribute('href', '/holons')
  })
})
