import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import PlatformGenesisPage from './page'

const request = vi.hoisted(() => vi.fn())
vi.mock('@/lib/operator-client', () => ({ operatorRequest: request }))
const id = 'a'.repeat(32)
const view = { operationId: id, version: 5, state: 'AwaitingConsent', chainType: 'Algorand', network: 'Devnet', walletAddress: 'reserve' }
beforeEach(() => { localStorage.clear(); vi.resetAllMocks() })

it('recovers a saved operator command using exact server version evidence without signing', async () => {
  const command = { id, kind: 'continue', body: { requestId: 'saved-request', version: 5 } }
  localStorage.setItem('azoa:operator:platform-genesis:v1', JSON.stringify({ operationId: id, command }))
  request.mockResolvedValueOnce(view).mockResolvedValueOnce(view).mockResolvedValueOnce({ ...view, version: 6 })
  render(<PlatformGenesisPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Recover original operation' }))
  const reconcile = await screen.findByRole('button', { name: 'Reconcile saved command with server' })
  fireEvent.click(reconcile)
  await screen.findByText(/server has not fenced this saved version/)
  expect(JSON.parse(localStorage.getItem('azoa:operator:platform-genesis:v1')!).command).toEqual(command)
  fireEvent.click(reconcile)
  await waitFor(() => expect(JSON.parse(localStorage.getItem('azoa:operator:platform-genesis:v1')!).command).toBeUndefined())
  expect(request.mock.calls.map(call => call[0])).toEqual([
    `platform-token/creation/${id}/refresh`, `platform-token/creation/${id}`, `platform-token/creation/${id}`,
  ])
})
