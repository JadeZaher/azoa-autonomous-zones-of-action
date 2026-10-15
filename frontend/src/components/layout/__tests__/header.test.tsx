import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

/**
 * Chrome that is orthogonal to the header's own dropdown-menu wiring is
 * stubbed out so this test isolates the one thing it needs to prove: opening
 * the user menu must not crash the tree. `useAzoa` is mocked because the
 * header calls it directly; `MobileSidebar` / `NetworkSwitcher` /
 * `DebugSwitcher` are unrelated Select/Sheet-based widgets pulled in by the
 * header for layout only.
 */
vi.mock('@/lib/azoa-context', () => ({
  useAzoa: () => ({
    user: { username: 'jade', email: 'jade@example.com' },
    logout: vi.fn(),
    logoutEverywhere: vi.fn(),
    defaultWallet: null,
  }),
}))

vi.mock('./sidebar', () => ({
  MobileSidebar: () => <div data-testid="mobile-sidebar-stub" />,
}))

vi.mock('@/components/network-switcher', () => ({
  NetworkSwitcher: () => <div data-testid="network-switcher-stub" />,
}))

vi.mock('@/components/debug-switcher', () => ({
  DebugSwitcher: () => <div data-testid="debug-switcher-stub" />,
}))

import { Header } from '../header'

describe('Header', () => {
  it('opens the user menu without crashing the tree', () => {
    render(<Header />)

    // Regression: `DropdownMenuLabel` used to sit directly under
    // `DropdownMenuContent`, with no `DropdownMenuGroup` wrapper. Base UI's
    // `Menu.GroupLabel` reads `MenuGroupRootContext` via
    // `useMenuGroupRootContext()`, which THROWS when that context is missing
    // — in production builds too — the first time this menu opens.
    expect(() => {
      fireEvent.click(screen.getByText('jade'))
    }).not.toThrow()

    expect(screen.getByText('Sign out')).toBeInTheDocument()
    expect(screen.getByText('Log out of all devices')).toBeInTheDocument()
  })
})
