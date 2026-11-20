import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ActionsPage from '../page'

const { request } = vi.hoisted(() => ({ request: vi.fn() }))

vi.mock('@/lib/azoa', () => ({
  azoa: { api: { request, listPublicActions: vi.fn(async () => ({ ok: true, value: [] })) } },
  isOk: (value: { ok: boolean }) => value.ok,
}))

vi.mock('@/lib/azoa-auth', () => ({
  useAzoa: () => ({ userId: 'user-builder-42' }),
  useAzoaAuth: () => ({
    userId: 'user-builder-42',
    isAuthenticated: true,
    user: { id: 'user-builder-42', username: 'builder', email: 'builder@azoa.example', dappRole: 'dapp:user' },
    loading: false,
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
  }),
}))

describe('ActionsPage first-mile builder improvements', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation(async (method: string, path: string) => {
      if (method === 'GET' && path === '/api/action/node-templates') return { ok: true, value: [] }
      if (method === 'GET' && path === '/api/action-designer') {
        return { ok: true, value: { available: false, provider: 'OpenRouter', model: 'test', disclosure: 'test', tenantId: 'user-builder-42' } }
      }
      if (method === 'GET' && path.startsWith('/api/action/user/')) return { ok: true, value: [] }
      if (method === 'POST' && path === '/api/action') return { ok: true, value: { id: 'act-1', name: 'Create Project' } }
      return { ok: true, value: [] }
    })
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('shows builder role guidance and 1-click starter blueprints that populate name and graph', async () => {
    const user = userEvent.setup()
    const writeText = vi.fn(async () => {})
    vi.spyOn(navigator.clipboard, 'writeText').mockImplementation(writeText)

    render(<ActionsPage />)
    fireEvent.click(screen.getByRole('button', { name: 'Builder' }))

    expect(await screen.findByText(/Builder role needed to save actions/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Open Role Assignment' })).toHaveAttribute('href', '/admin/roles')

    await user.click(screen.getByRole('button', { name: 'Copy User ID' }))
    expect(writeText).toHaveBeenCalledWith('user-builder-42')

    const blueprintRegion = screen.getByRole('region', { name: 'Starter workflow blueprints' })
    expect(blueprintRegion).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Gated Branch' }))
    await waitFor(() => {
      expect(screen.getByPlaceholderText('My Action')).toHaveValue('Gated Branch')
    })
  })
})
