import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { NftLifecycleView } from 'azoa-sdk'
import { NftLifecyclePanel } from './nft-lifecycle-panel'

const api = vi.hoisted(() => ({ refreshNftLifecycle: vi.fn(), acceptNftLifecycle: vi.fn(), cancelNftLifecycle: vi.fn() }))
vi.mock('@/lib/azoa-auth', () => ({ useAzoaAuth: () => ({ userId: 'user' }) }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
const operation: NftLifecycleView = { version: 3, preparationGeneration: 0, operation: 'Mint', feeQuote: null, actorUserId: 'user', recipientUserId: 'user', operationId: 'opaque', nftId: 'nft', state: 'Submitted', stage: 'Create', assetId: null, transactionId: 'tx', detail: null }

describe('NFT lifecycle status', () => {
  beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })
  it('does not present a submitted transaction as completed', () => {
    render(<NftLifecyclePanel value={operation} onChange={vi.fn()} />)
    expect(screen.getByText('Submitted to the chain. Confirmation is still pending.')).toBeInTheDocument()
    expect(screen.queryByText('Completed with confirmed chain evidence.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Accept NFT' })).not.toBeInTheDocument()
  })
  it('requires an explicit creator action before destruction', async () => {
    const next = { ...operation, state: 'Submitted' }
    api.acceptNftLifecycle.mockResolvedValue({ ok: true, value: next })
    const onChange = vi.fn()
    render(<NftLifecyclePanel value={{ ...operation, state: 'AwaitingCreatorDestruction' }} onChange={onChange} />)
    expect(api.acceptNftLifecycle).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Authorize destruction' }))
    await waitFor(() => expect(onChange).toHaveBeenCalledWith(next))
    expect(api.acceptNftLifecycle).toHaveBeenCalledWith('opaque', expect.objectContaining({
      expectedVersion: 3, state: 'AwaitingCreatorDestruction', stage: 'Create', generation: 0,
    }))
  })
  it('saves the exact acceptance before sending and retries it after a lost response', async () => {
    const value = { ...operation, state: 'AwaitingRecipientAcceptance' as const }
    api.acceptNftLifecycle.mockImplementationOnce(async (_id, request) => {
      expect(JSON.parse(localStorage.getItem('azoa:nft-stage-acceptance:user:opaque:3')!)).toEqual(request)
      throw new Error('Response lost')
    }).mockResolvedValueOnce({ ok: true, value: operation })
    render(<NftLifecyclePanel value={value} onChange={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Accept NFT' }))
    await screen.findByText('Response lost')
    const original = api.acceptNftLifecycle.mock.calls[0][1]
    fireEvent.click(screen.getByRole('button', { name: 'Accept NFT' }))
    await waitFor(() => expect(api.acceptNftLifecycle).toHaveBeenCalledTimes(2))
    expect(api.acceptNftLifecycle.mock.calls[1][1]).toEqual(original)
  })
  it('offers replacement acceptance only for an explicitly eligible generation', async () => {
    const pending = { ...operation, state: 'Preparing' as const, preparationGeneration: 1, version: 8 }
    const { rerender } = render(<NftLifecyclePanel value={pending} onChange={vi.fn()} />)
    expect(screen.queryByRole('button', { name: 'Authorize replacement stage' })).not.toBeInTheDocument()
    api.acceptNftLifecycle.mockResolvedValueOnce({ ok: true, value: operation })
    rerender(<NftLifecyclePanel value={{ ...pending, needsStageAcceptance: true }} onChange={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Authorize replacement stage' }))
    await waitFor(() => expect(api.acceptNftLifecycle).toHaveBeenCalledWith('opaque', expect.objectContaining({
      expectedVersion: 8, generation: 1, state: 'Preparing', stage: 'Create',
    })))
  })
})
