import { beforeEach, expect, it, vi } from 'vitest'
import type { NftLifecycleView } from 'azoa-sdk'
import { clearResolvedNftRequests } from './nft-request-recovery'

const api = vi.hoisted(() => ({ getNftLifecycleByRequest: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })
const terminal = { operation: 'Mint', operationId: 'durable', state: 'Cancelled' } as NftLifecycleView
it('clears a lost-response request only after the server correlates its key to the cancelled operation', async () => {
  localStorage.setItem('azoa:nft-mint:user', JSON.stringify({ key: 'lost-response' }))
  api.getNftLifecycleByRequest.mockResolvedValue({ ok: true, value: terminal })
  expect(await clearResolvedNftRequests('user', terminal)).toBe(true)
  expect(api.getNftLifecycleByRequest).toHaveBeenCalledWith('Mint', 'lost-response')
  expect(localStorage.getItem('azoa:nft-mint:user')).toBeNull()
})
it('preserves the key when authoritative lookup is absent or belongs to another request', async () => {
  const saved = JSON.stringify({ key: 'unresolved' })
  localStorage.setItem('azoa:nft-mint:user', saved)
  api.getNftLifecycleByRequest.mockResolvedValueOnce({ ok: false, error: { status: 404 } })
    .mockResolvedValueOnce({ ok: true, value: { ...terminal, operationId: 'other' } })
  expect(await clearResolvedNftRequests('user', terminal)).toBe(false)
  expect(await clearResolvedNftRequests('user', terminal)).toBe(false)
  expect(localStorage.getItem('azoa:nft-mint:user')).toBe(saved)
})
