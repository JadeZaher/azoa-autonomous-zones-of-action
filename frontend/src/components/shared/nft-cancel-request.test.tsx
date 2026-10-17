import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { NftCancelRequest } from './nft-cancel-request'
const api = vi.hoisted(() => ({ cancelNftRequest: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })
it('retains an uncertain request when cancellation conflicts with submission', async () => {
  localStorage.setItem('saved', JSON.stringify({ key: 'key' }))
  api.cancelNftRequest.mockResolvedValue({ ok: false, error: { message: 'Already submitted' } })
  const done = vi.fn()
  render(<NftCancelRequest operation="Mint" requestKey="key" storageKey="saved" onCancelled={done} />)
  fireEvent.click(screen.getByRole('button', { name: 'Cancel this request before editing' }))
  await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Already submitted'))
  expect(localStorage.getItem('saved')).not.toBeNull()
  expect(done).not.toHaveBeenCalled()
})
it('unlocks only after the server returns the cancellation tombstone', async () => {
  localStorage.setItem('saved', JSON.stringify({ key: 'key' }))
  const value = { operationId: 'request', state: 'Cancelled' }
  api.cancelNftRequest.mockResolvedValue({ ok: true, value })
  const done = vi.fn()
  render(<NftCancelRequest operation="Mint" requestKey="key" storageKey="saved" onCancelled={done} />)
  fireEvent.click(screen.getByRole('button', { name: 'Cancel this request before editing' }))
  await waitFor(() => expect(done).toHaveBeenCalledWith(value))
  expect(localStorage.getItem('saved')).toBeNull()
})
