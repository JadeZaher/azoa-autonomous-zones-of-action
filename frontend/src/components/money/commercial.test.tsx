import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { safeReturnUrl } from '@/components/auth/member-handoff'
import { Sidebar } from '@/components/layout/sidebar'
import { RideAlongView } from '@/components/money/ride-along-view'
import DocumentsPage from '@/app/(dashboard)/documents/page'

vi.mock('next/navigation', () => ({
  usePathname: () => '/documents',
}))

vi.mock('@/lib/azoa-auth', () => ({
  useAzoaAuth: () => ({
    userId: 'user-123',
    isAuthenticated: true,
    user: { id: 'user-123', username: 'member', email: 'member@azoa.example' },
    logout: vi.fn(),
  }),
}))

vi.mock('@/lib/azoa', () => ({
  azoa: {
    session: { token: 'test-jwt', userId: 'user-123' },
  },
}))

vi.mock('@/lib/runtime-config', () => ({
  readInitialApiUrl: () => 'https://api.azoa.example',
}))

describe('safeReturnUrl', () => {
  afterEach(() => {
    delete process.env.NEXT_PUBLIC_ALLOWED_RETURN_ORIGINS
  })

  it('permits portfolio, verification, ride, and invoice return destinations while rejecting unsafe schemes', () => {
    expect(safeReturnUrl('https://partner.example/portfolio')).toBe(true)
    expect(safeReturnUrl('https://partner.example/portfolio/withdraw')).toBe(true)
    expect(safeReturnUrl('https://partner.example/portfolio/add-funds?amount=25')).toBe(true)
    expect(safeReturnUrl('https://partner.example/settings/verification')).toBe(true)
    expect(safeReturnUrl('https://partner.example/ride?linkId=lnk_1')).toBe(true)
    expect(safeReturnUrl('https://partner.example/invoices/inv_9?status=paid')).toBe(true)
    expect(safeReturnUrl('http://localhost:3001/portfolio')).toBe(true)
    expect(safeReturnUrl('/ride?linkId=lnk_1')).toBe(true)

    process.env.NEXT_PUBLIC_ALLOWED_RETURN_ORIGINS = 'https://custom-host.example'
    expect(safeReturnUrl('https://custom-host.example/app/done?ok=1')).toBe(true)

    expect(safeReturnUrl('javascript:alert(1)')).toBe(false)
    expect(safeReturnUrl('data:text/html,<script>alert(1)</script>')).toBe(false)
    expect(safeReturnUrl('http://evil.example/portfolio')).toBe(false)
    expect(safeReturnUrl('https://user@partner.example/portfolio')).toBe(false)
    expect(safeReturnUrl('https://evil.example/arbitrary')).toBe(false)
    expect(safeReturnUrl('//evil.example/portfolio')).toBe(false)
  })
})

describe('Sidebar commercial navigation & audience gating', () => {
  afterEach(() => {
    cleanup()
    delete process.env.NEXT_PUBLIC_AZOA_OPERATOR_CONSOLE
  })

  it('shows Documents & invoices to all members and hides Admin/Tests unless operator console is enabled', () => {
    render(<Sidebar />)
    expect(screen.getByRole('link', { name: 'Documents & invoices' }).getAttribute('href')).toBe('/documents')
    expect(screen.queryByRole('link', { name: 'Key Rotation' })).toBeNull()
    expect(screen.queryByRole('link', { name: 'Tests' })).toBeNull()
  })

  it('shows operator navigation when NEXT_PUBLIC_AZOA_OPERATOR_CONSOLE=true', () => {
    process.env.NEXT_PUBLIC_AZOA_OPERATOR_CONSOLE = 'true'
    render(<Sidebar />)
    expect(screen.getByRole('link', { name: 'Key Rotation' })).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Tests' })).toBeTruthy()
  })
})

describe('Documents hub & Ride-Along pages', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/documents')
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('renders Documents hub with all five commercial SDK panels wired to transport', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url.endsWith('/access-links')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.endsWith('/identomat/status')) {
        return new Response(
          JSON.stringify({
            provider: 'Identomat',
            status: 'PendingVendorCredentials',
            deferralLogged: true,
            deferredReason: 'Operator provisioning Identomat keys.',
          }),
          { status: 200 },
        )
      }
      if (url.endsWith('/invoices')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.endsWith('/payment-requests')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.endsWith('/receipts')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.endsWith('/documents')) return new Response(JSON.stringify([]), { status: 200 })
      if (url.endsWith('/deferrals')) return new Response(JSON.stringify([]), { status: 200 })
      return new Response('{}', { status: 200 })
    })
    vi.stubGlobal('fetch', fetchMock)

    render(<DocumentsPage />)

    expect(await screen.findByText('Signed Access Link')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Issue Access Link' })).toBeTruthy()
    expect(await screen.findByText(/Operator provisioning Identomat keys/)).toBeTruthy()
    expect(screen.getByText('Invoices & Payment Requests')).toBeTruthy()
    expect(screen.getByText('Ownership Receipts')).toBeTruthy()
    expect(screen.getByText('Account Documents & Reports')).toBeTruthy()
  })

  it('fails closed on missing or expired ride-along links and renders invoice settlement when verified', async () => {
    window.history.replaceState({}, '', '/ride')
    const { unmount } = render(<RideAlongView />)
    expect(await screen.findByText('Access link unavailable')).toBeTruthy()
    expect(screen.getByText(/missing its signature/i)).toBeTruthy()
    unmount()

    window.history.replaceState(
      {},
      '',
      '/ride?linkId=lnk_1&scope=invoice_pay&sub=user-123&exp=2099-01-01T00:00:00Z&sig=sig_ok',
    )

    const user = userEvent.setup()
    let paid = false
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = typeof input === 'string' ? input : input.toString()
        if (url.includes('/access-links/lnk_1/verify')) {
          return new Response(
            JSON.stringify({
              valid: true,
              expired: false,
              link: {
                linkId: 'lnk_1',
                externalSubject: 'user-123',
                scope: 'invoice_pay',
                targetResourceId: 'inv_1',
                returnUrl: 'https://partner.example/portfolio/add-funds',
                signedUrl: 'https://azoa.example/ride?linkId=lnk_1',
                signature: 'sig_ok',
                expiresAt: '2099-01-01T00:00:00Z',
                provider: 'azoa-hmac-v1',
                deferralStatus: 'active',
              },
            }),
            { status: 200 },
          )
        }
        if (url.endsWith('/invoices/inv_1/pay') && init?.method === 'POST') {
          paid = true
          return new Response(
            JSON.stringify({
              id: 'inv_1',
              invoiceNumber: 'INV-101',
              issuerSubject: 'azoa',
              payerSubject: 'user-123',
              currencyCode: 'USD',
              subtotalMinorUnits: 7500,
              feeMinorUnits: 0,
              totalMinorUnits: 7500,
              lineItems: [],
              status: 'paid',
              createdAt: '2026-11-15T00:00:00Z',
            }),
            { status: 200 },
          )
        }
        if (url.endsWith('/invoices')) {
          return new Response(
            JSON.stringify([
              {
                id: 'inv_1',
                invoiceNumber: 'INV-101',
                issuerSubject: 'azoa',
                payerSubject: 'user-123',
                currencyCode: 'USD',
                subtotalMinorUnits: 7500,
                feeMinorUnits: 0,
                totalMinorUnits: 7500,
                lineItems: [],
                status: paid ? 'paid' : 'open',
                createdAt: '2026-11-15T00:00:00Z',
              },
            ]),
            { status: 200 },
          )
        }
        if (url.endsWith('/payment-requests')) {
          return new Response(JSON.stringify([]), { status: 200 })
        }
        return new Response('[]', { status: 200 })
      }),
    )

    render(<RideAlongView />)

    expect(await screen.findByText('Review and settle invoice')).toBeTruthy()
    expect(screen.getByRole('link', { name: /Return to platform/i }).getAttribute('href')).toBe(
      'https://partner.example/portfolio/add-funds',
    )
    expect(screen.getByText('75.00 USD')).toBeTruthy()

    await user.click(screen.getByRole('button', { name: 'Pay Invoice' }))
    await waitFor(() => expect(screen.getByText('paid')).toBeTruthy())
  })
})
