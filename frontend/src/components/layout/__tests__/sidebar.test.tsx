import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { MobileSidebar } from '../sidebar'

vi.mock('next/navigation', () => ({ usePathname: () => '/overview' }))

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
