import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import type { NftLifecycleView } from 'azoa-sdk'
import { NftReplacementCosts } from './nft-replacement-costs'

const api = vi.hoisted(() => ({
  quoteNftReplacementCosts: vi.fn(), acceptNftReplacementCosts: vi.fn(),
  reconcileNftReplacementCosts: vi.fn(), retireNftReplacementCosts: vi.fn(),
}))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))

const key = 'azoa.nftReplacementCosts.v1.user:op'
const limits = { networkFee: '5000', retainedRent: '9646560', protocolLevy: '10000000' }
const offerFor = (generation: number, quoteId: string) => ({
  quote: { quoteId, lifecycleId: 'op', actorUserId: 'user', preparationGeneration: generation, stage: 'Create', chainType: 'Solana',
    network: 'Devnet', payerAddress: 'payer', expiresAt: '2099-01-01T00:00:00Z', limits },
  authorization: { quoteId, quoteHash: `${quoteId}-hash`, limits },
})
const attempt = (generation: number, version: number): NftLifecycleView => ({
  version, preparationGeneration: generation, needsReplacementCosts: true, operation: 'Mint', feeQuote: null,
  actorUserId: 'user', recipientUserId: 'user', operationId: 'op', nftId: 'nft', state: 'Preparing', stage: 'Create',
  assetId: null, transactionId: null, detail: null,
})

beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('saves the exact acceptance before sending and replaces it only after reconciliation proves it obsolete', async () => {
  api.quoteNftReplacementCosts.mockResolvedValueOnce({ ok: true, value: offerFor(1, 'first') })
  api.acceptNftReplacementCosts.mockImplementationOnce(async (_id: string, request: unknown) => {
    expect(JSON.parse(localStorage.getItem(key)!).acceptance).toEqual(request)
    throw new Error('response lost')
  })
  const view = render(<NftReplacementCosts actor="user" value={attempt(1, 8)} onChange={vi.fn()} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Review replacement costs' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Accept replacement costs' }))
  await screen.findByText('response lost')
  expect(api.quoteNftReplacementCosts).toHaveBeenCalledWith('op', expect.objectContaining({
    expectedVersion: 8, state: 'Preparing', stage: 'Create', generation: 1,
  }))
  const original = api.acceptNftReplacementCosts.mock.calls[0][1]
  expect(screen.queryByRole('button', { name: 'Review replacement costs' })).not.toBeInTheDocument()
  view.unmount()

  api.retireNftReplacementCosts.mockRejectedValueOnce(new Error('retirement response lost'))
  render(<NftReplacementCosts actor="user" value={attempt(1, 8)} onChange={vi.fn()} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Retire uncommitted cost acceptance' }))
  await screen.findByText('retirement response lost')
  expect(api.retireNftReplacementCosts).toHaveBeenCalledWith('op', original)
  expect(screen.queryByRole('button', { name: 'Review replacement costs' })).not.toBeInTheDocument()

  api.reconcileNftReplacementCosts.mockResolvedValueOnce({ ok: true, value: { status: 'Obsolete', lifecycle: attempt(1, 9) } })
  fireEvent.click(screen.getByRole('button', { name: 'Reconcile cost acceptance' }))
  const fresh = await screen.findByRole('button', { name: 'Review replacement costs' })
  expect(api.reconcileNftReplacementCosts).toHaveBeenCalledWith('op', original)
  api.quoteNftReplacementCosts.mockResolvedValueOnce({ ok: true, value: offerFor(1, 'second') })
  fireEvent.click(fresh)
  await screen.findByRole('button', { name: 'Accept replacement costs' })
  expect(JSON.parse(localStorage.getItem(key)!).acceptance).toBeUndefined()
  expect(JSON.parse(localStorage.getItem(`${key}.${original.stage.requestId}`)!).acceptance).toEqual(original)
  expect(api.acceptNftReplacementCosts).toHaveBeenCalledTimes(1)
})

it('lets a later attempt review its own costs once the earlier saved acceptance is proven applied', async () => {
  const earlier = offerFor(1, 'earlier')
  const stage = { requestId: 'earlier-request', expectedVersion: 8, state: 'Preparing', stage: 'Create', generation: 1 }
  const acceptance = { stage, costs: earlier.authorization }
  localStorage.setItem(key, JSON.stringify({ stage, offer: earlier, acceptance }))
  const later = attempt(2, 12)
  const onChange = vi.fn()
  render(<NftReplacementCosts actor="user" value={later} onChange={onChange} />)
  expect(await screen.findByRole('button', { name: 'Reconcile cost acceptance' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Review replacement costs' })).not.toBeInTheDocument()

  api.reconcileNftReplacementCosts.mockResolvedValueOnce({ ok: true, value: { status: 'Applied', lifecycle: later } })
  fireEvent.click(screen.getByRole('button', { name: 'Reconcile cost acceptance' }))
  await screen.findByText('The saved acceptance covered an earlier attempt. This attempt needs its own cost review.')
  expect(onChange).toHaveBeenCalledWith(later)
  api.quoteNftReplacementCosts.mockResolvedValueOnce({ ok: true, value: offerFor(2, 'later') })
  fireEvent.click(screen.getByRole('button', { name: 'Review replacement costs' }))
  await screen.findByRole('button', { name: 'Accept replacement costs' })
  await waitFor(() => expect(api.quoteNftReplacementCosts).toHaveBeenCalledWith('op', expect.objectContaining({ expectedVersion: 12, generation: 2 })))
  expect(api.acceptNftReplacementCosts).not.toHaveBeenCalled()
})
