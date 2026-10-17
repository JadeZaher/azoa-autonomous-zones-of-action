import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { NftWorkflowCosts } from './nft-workflow-costs'

const api = vi.hoisted(() => ({ quoteNftWorkflowCosts: vi.fn(), acceptNftWorkflowCosts: vi.fn(), reconcileNftWorkflowCosts: vi.fn(), retireNftWorkflowCosts: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
vi.mock('@/components/shared/nft-lifecycle-panel', () => ({ NftLifecyclePanel: () => <p>Admitted lifecycle</p> }))
const offer = { executionId: 'execution', executionVersion: 4, offer: {
  quote: { chainType: 'Solana', network: 'Devnet', payerAddress: 'payer', expiresAt: '2099-01-01T00:00:00Z',
    limits: { networkFee: '1', retainedRent: '2', protocolLevy: '3' } },
  authorization: { quoteId: 'original', quoteHash: 'hash', limits: { networkFee: '1', retainedRent: '2', protocolLevy: '3' } },
} }
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('retains the original request after a lost retirement response and only replaces it after proof', async () => {
  const request = { requestId: 'original-request', executionId: 'execution', expectedExecutionVersion: 4, authorization: offer.offer.authorization }
  const key = 'azoa.nftWorkflowCosts.v1.actor:execution'
  const saved = { disclosure: offer, acceptance: request }
  localStorage.setItem(key, JSON.stringify(saved))
  localStorage.setItem(`${key}.${request.requestId}`, JSON.stringify(saved))
  api.retireNftWorkflowCosts.mockRejectedValueOnce(new Error('retirement response lost'))
  const view = render(<NftWorkflowCosts scope="actor" executionId="execution" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Retire uncommitted acceptance' }))
  await screen.findByText('retirement response lost')
  expect(api.retireNftWorkflowCosts).toHaveBeenCalledWith(request)
  expect(screen.queryByRole('button', { name: 'Review a fresh cost offer' })).not.toBeInTheDocument()
  expect(JSON.parse(localStorage.getItem(key)!).acceptance).toEqual(request)
  view.unmount()
  api.reconcileNftWorkflowCosts.mockResolvedValueOnce({ ok: true, value: { state: 'Obsolete' } })
  render(<NftWorkflowCosts scope="actor" executionId="execution" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Reconcile saved acceptance' }))
  const fresh = await screen.findByRole('button', { name: 'Review a fresh cost offer' })
  api.quoteNftWorkflowCosts.mockResolvedValueOnce({ ok: true, value: { ...offer, executionVersion: 5 } })
  fireEvent.click(fresh)
  await screen.findByRole('button', { name: 'Accept these costs' })
  expect(JSON.parse(localStorage.getItem(key)!).acceptance).toBeUndefined()
  expect(JSON.parse(localStorage.getItem(`${key}.${request.requestId}`)!).acceptance).toEqual(request)
  expect(api.acceptNftWorkflowCosts).not.toHaveBeenCalled()
})

it('keeps the admitted lifecycle when acceptance wins retirement', async () => {
  const acceptance = { requestId: 'original-request', executionId: 'execution', expectedExecutionVersion: 4, authorization: offer.offer.authorization }
  localStorage.setItem('azoa.nftWorkflowCosts.v1.actor:execution', JSON.stringify({ disclosure: offer, acceptance }))
  api.retireNftWorkflowCosts.mockResolvedValueOnce({ ok: true, value: { state: 'Applied', lifecycle: { operationId: 'original' } } })
  render(<NftWorkflowCosts scope="actor" executionId="execution" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Retire uncommitted acceptance' }))
  await screen.findByText('Admitted lifecycle')
  expect(screen.queryByRole('button', { name: 'Review a fresh cost offer' })).not.toBeInTheDocument()
  expect(api.acceptNftWorkflowCosts).not.toHaveBeenCalled()
})

it('discloses without accepting, persists before send, and restores exact request after a lost response', async () => {
  api.quoteNftWorkflowCosts.mockResolvedValueOnce({ ok: true, value: offer })
  api.acceptNftWorkflowCosts.mockImplementationOnce(async (request: unknown) => {
    expect(JSON.parse(localStorage.getItem('azoa.nftWorkflowCosts.v1.actor:execution')!).acceptance).toEqual(request)
    throw new Error('response lost')
  })
  const view = render(<NftWorkflowCosts scope="actor" executionId="execution" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Review creation costs' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Accept these costs' }))
  await screen.findByText('response lost')
  expect(api.acceptNftWorkflowCosts).toHaveBeenCalledTimes(1)
  const request = api.acceptNftWorkflowCosts.mock.calls[0][0]
  view.unmount()
  api.reconcileNftWorkflowCosts.mockResolvedValueOnce({ ok: true, value: { state: 'Unresolved' } })
  render(<NftWorkflowCosts scope="actor" executionId="execution" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Reconcile saved acceptance' }))
  await waitFor(() => expect(api.reconcileNftWorkflowCosts).toHaveBeenCalledWith(request))
  expect(api.quoteNftWorkflowCosts).toHaveBeenCalledTimes(1)
  expect(api.acceptNftWorkflowCosts).toHaveBeenCalledTimes(1)
})

it('does not save a late disclosure after the actor changes', async () => {
  let resolve!: (value: unknown) => void
  api.quoteNftWorkflowCosts.mockReturnValueOnce(new Promise(done => { resolve = done }))
  const view = render(<NftWorkflowCosts scope="old" executionId="execution" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Review creation costs' }))
  view.rerender(<NftWorkflowCosts scope="new" executionId="execution" />)
  await act(async () => resolve({ ok: true, value: offer }))
  expect(localStorage.getItem('azoa.nftWorkflowCosts.v1.old:execution')).toBeNull()
  expect(localStorage.getItem('azoa.nftWorkflowCosts.v1.new:execution')).toBeNull()
  expect(screen.queryByRole('button', { name: 'Accept these costs' })).not.toBeInTheDocument()
})
