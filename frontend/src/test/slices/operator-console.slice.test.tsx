import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { NextRequest } from 'next/server'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { EthereumPreparationView, SagaStepView, WalletIdentityMigrationView } from 'azoa-sdk'
import * as operatorRoute from '@/app/api/operator/[...path]/route'
import { EthereumRecovery } from '@/components/operator/chain-recovery'
import OperatorFeesPage from '@/app/operator/(console)/fees/page'
import OperatorSagasPage from '@/app/operator/(console)/sagas/page'
import OperatorWalletIdentitiesPage from '@/app/operator/(console)/wallet-identities/page'
import { SLICE_API_ORIGIN, apiUrl, azoaOk } from '../msw/api'
import { renderSlice } from './render-slice'

vi.mock('next/headers', () => ({ cookies: async () => ({ get: () => ({ value: 'operator-session-token' }) }) }))

const CONSOLE_ORIGIN = 'http://console.slice.test'
let restoreFetch: (() => void) | null = null

/**
 * Console pages fetch the relative BFF path `/api/operator/...`; this hands each call to the REAL route handler
 * (allowlist, bare flags, sanitizer), whose upstream fetch reaches the MSW-faked API. Body shapes below follow the
 * named C# controllers, so a bare-vs-envelope mismatch in `operator-routes.ts` fails here.
 */
function routeConsoleThroughBff() {
  const upstreamFetch = globalThis.fetch
  globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url
    if (!url.startsWith('/api/operator/')) return upstreamFetch(input, init)
    const target = new URL(url, CONSOLE_ORIGIN)
    const method = (init?.method ?? 'GET') as 'GET' | 'PUT' | 'POST'
    const headers = new Headers(init?.headers)
    if (method !== 'GET') headers.set('origin', CONSOLE_ORIGIN)
    const request = new NextRequest(target, { method, headers, body: init?.body ?? undefined })
    const path = target.pathname.slice('/api/operator/'.length).split('/')
    const handler = method === 'GET' ? operatorRoute.GET : method === 'PUT' ? operatorRoute.PUT : operatorRoute.POST
    return handler(request, { params: Promise.resolve({ path }) })
  }
  return () => { globalThis.fetch = upstreamFetch }
}

beforeEach(() => {
  vi.stubEnv('API_URL', SLICE_API_ORIGIN)
  restoreFetch = routeConsoleThroughBff()
})
afterEach(() => {
  restoreFetch?.()
  vi.unstubAllEnvs()
})

const preparation: EthereumPreparationView = {
  id: 'prep-7', network: 'Devnet', sender: `0x${'a'.repeat(40)}`, state: 'Broadcast', nonce: '7',
  transactionId: `0x${'b'.repeat(64)}`, preparationKey: 'platform:7', updatedAt: '2026-10-17T10:00:00Z',
}

describe('operator console slice (real BFF route)', () => {
  it('lists stuck platform preparations from the bare Ethereum recovery list', async () => {
    // `EthereumOperatorRecoveryController.List` returns `Ok(IReadOnlyList<EthereumPreparationView>)`: no envelope.
    const handlers = [http.get(apiUrl('/api/operator/ethereum/recovery/Devnet'), () => HttpResponse.json([preparation]))]
    await renderSlice(<EthereumRecovery />, { apis: [{ handlers }], session: 'anonymous' })

    expect(await screen.findByText(/nonce 7/)).toHaveTextContent(`sender ${preparation.sender}`)
    expect(screen.queryByText(/No platform preparations need recovery/)).not.toBeInTheDocument()
  })

  it('reads the bare wallet-identity view, clears a conflict (AZOAResult<bool>) and shows the re-read status', async () => {
    let view: WalletIdentityMigrationView = {
      status: 'Blocked', version: 3, conflicts: [{ userId: 'user-1', expectedIdentity: 'algorand:ADDR1', problem: 'Claimed by two accounts' }],
    }
    const cleared: unknown[] = []
    const handlers = [
      // `WalletIdentityOperatorController.Get` returns the bare `View`; `Clear` returns `AZOAResult<bool>`.
      http.get(apiUrl('/api/operator/wallet-identities'), () => HttpResponse.json(view)),
      http.post(apiUrl('/api/operator/wallet-identities/clear-conflicting-binding'), async ({ request }) => {
        cleared.push(await request.json())
        view = { status: 'Completed', version: 4, conflicts: [] }
        return HttpResponse.json(azoaOk(true))
      }),
    ]
    const { user } = await renderSlice(<OperatorWalletIdentitiesPage />, { apis: [{ handlers }], session: 'anonymous' })

    expect(await screen.findByText('Version 3 · 1 conflict(s)')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Clear binding…' }))
    await user.type(screen.getByLabelText(/Why this binding is stale/), 'Old account was merged into user-2.')
    await user.click(screen.getByRole('button', { name: 'Clear conflicting binding' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Cleared. Migration is Completed with 0 conflict(s) left.')
    expect(screen.getByText('Version 4 · 0 conflict(s)')).toBeInTheDocument()
    expect(cleared).toEqual([{ version: 3, userId: 'user-1', expectedIdentity: 'algorand:ADDR1', reason: 'Old account was merged into user-2.' }])
  })

  it('shows generic saga steps from the bare dead-letter list and says how many workflow steps it hid', async () => {
    const step = (sagaName: string, id: string): SagaStepView => ({
      id, sagaName, stepName: 'deliver', correlationKey: `run-${id.slice(0, 4)}`, status: 'DeadLettered', isCompensation: false,
      attemptCount: 5, lastError: 'Timed out', nextRunAt: '2026-10-17T10:00:00Z', updatedAt: '2026-10-17T10:00:00Z',
    })
    // `SagaOperatorController.ListDeadLetters` returns `Ok(List<SagaStepView>)`: no envelope.
    const handlers = [http.get(apiUrl('/api/admin/sagas/dead-letters'), () => HttpResponse.json([
      step('webhook-delivery', '11111111-1111-4111-8111-111111111111'),
      step('action-workflow', '22222222-2222-4222-8222-222222222222'),
    ]))]
    await renderSlice(<OperatorSagasPage />, { apis: [{ handlers }], session: 'anonymous' })

    expect(await screen.findByText('webhook-delivery · deliver')).toBeInTheDocument()
    expect(screen.queryByText('action-workflow · deliver')).not.toBeInTheDocument()
    expect(screen.getByRole('note')).toHaveTextContent('1 workflow step(s) on this page are hidden here')
  })

  it('refuses an unwrapped body on an enveloped route instead of rendering it as empty', async () => {
    // The fee schedule route is not marked bare: a body without `isError` is contract drift, surfaced as a 502.
    const handlers = [
      http.get(apiUrl('/api/node-governance/fee-schedule'), () => HttpResponse.json({ version: 2 })),
      http.get(apiUrl('/api/node-governance/fee-audit'), () => HttpResponse.json(azoaOk([]))),
    ]
    await renderSlice(<OperatorFeesPage />, { apis: [{ handlers }], session: 'anonymous' })

    await waitFor(() => expect(screen.getByText('The node returned an invalid operator response.')).toBeInTheDocument())
  })
})
