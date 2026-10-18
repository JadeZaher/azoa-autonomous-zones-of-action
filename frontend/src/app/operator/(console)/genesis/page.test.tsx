import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import PlatformGenesisPage from './page'

const request = vi.hoisted(() => vi.fn())
vi.mock('@/lib/operator-client', () => ({ operatorRequest: request }))
const id = 'a'.repeat(32)
const requestId = '11111111-1111-4111-8111-111111111111'
const view = { operationId: id, version: 5, state: 'AwaitingConsent', chainType: 'Algorand', network: 'Devnet', walletAddress: 'reserve' }
const urlParams = () => new URLSearchParams(window.location.search)
beforeEach(() => { window.history.replaceState(null, '', '/operator/genesis'); vi.resetAllMocks() })

it('recovers a command saved in the page URL using exact server version evidence without signing', async () => {
  window.history.replaceState(null, '', `/operator/genesis?operation=${id}&command=continue&version=5&request=${requestId}`)
  request.mockResolvedValueOnce(view).mockResolvedValueOnce(view).mockResolvedValueOnce({ ...view, version: 6 })
  render(<PlatformGenesisPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Recover original operation' }))
  const reconcile = await screen.findByRole('button', { name: 'Reconcile saved command with server' })
  fireEvent.click(reconcile)
  await screen.findByText(/server has not fenced this saved version/)
  expect(urlParams().get('request')).toBe(requestId)
  fireEvent.click(reconcile)
  await waitFor(() => expect(urlParams().get('command')).toBeNull())
  expect(urlParams().get('operation')).toBe(id)
  expect(request.mock.calls.map(call => call[0])).toEqual([
    `platform-token/creation/${id}/refresh`, `platform-token/creation/${id}`, `platform-token/creation/${id}`,
  ])
})

it('replays the exact saved command body after a lost response', async () => {
  window.history.replaceState(null, '', `/operator/genesis?operation=${id}`)
  request.mockResolvedValueOnce({ ...view, state: 'Admitted' })
    .mockRejectedValueOnce(new Error('Response was lost'))
    .mockResolvedValueOnce({ ...view, state: 'Preparing', version: 6 })
  render(<PlatformGenesisPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Recover original operation' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Authorize and continue genesis' }))
  await screen.findByText('Response was lost')
  const saved = urlParams()
  expect(saved.get('command')).toBe('continue')
  fireEvent.click(screen.getByRole('button', { name: 'Replay saved continue' }))
  await waitFor(() => expect(urlParams().get('command')).toBeNull())
  const continues = request.mock.calls.filter(call => call[0].endsWith('/continue'))
  expect(continues).toHaveLength(2)
  expect(continues[1][1].body).toEqual(continues[0][1].body)
  expect(continues[0][1].body).toEqual({ version: 5, requestId: saved.get('request') })
})

it('ignores a malformed command in the URL instead of replaying it', async () => {
  window.history.replaceState(null, '', `/operator/genesis?operation=${id}&command=continue&version=5&request=not-a-uuid`)
  request.mockResolvedValueOnce(view)
  render(<PlatformGenesisPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Recover original operation' }))
  await screen.findByRole('button', { name: 'Request cost quote' })
  expect(screen.queryByRole('button', { name: /Replay saved/ })).not.toBeInTheDocument()
})
