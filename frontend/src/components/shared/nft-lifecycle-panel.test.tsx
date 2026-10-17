import { cleanup, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { NftLifecycleView } from 'azoa-sdk'
import { NftLifecyclePanel } from './nft-lifecycle-panel'

const api = vi.hoisted(() => ({ refreshNftLifecycle: vi.fn(), acceptNftLifecycle: vi.fn(), cancelNftLifecycle: vi.fn(), renewNftLifecycleAuthority: vi.fn() }))
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
  it('renews lapsed requester authority with one saved request and shows recipients that they must wait', async () => {
    const lapsed = { ...operation, state: 'AwaitingRecipientAcceptance' as const, recipientUserId: 'holder', needsActorContinuation: true }
    api.renewNftLifecycleAuthority.mockImplementationOnce(async (_id: string, request: unknown) => {
      expect(JSON.parse(localStorage.getItem('azoa:nft-actor-renewal:user:opaque:3')!)).toEqual(request)
      throw new Error('Response lost')
    }).mockResolvedValueOnce({ ok: true, value: { ...lapsed, needsActorContinuation: false, version: 4 } })
    const onChange = vi.fn()
    render(<NftLifecyclePanel value={lapsed} onChange={onChange} />)
    expect(screen.queryByText("The requester's authority has lapsed; this stage continues after they renew it.")).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Renew my authority' }))
    await screen.findByText('Response lost')
    fireEvent.click(screen.getByRole('button', { name: 'Renew my authority' }))
    await waitFor(() => expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ needsActorContinuation: false })))
    expect(api.renewNftLifecycleAuthority.mock.calls[1][1]).toEqual(api.renewNftLifecycleAuthority.mock.calls[0][1])
    expect(api.renewNftLifecycleAuthority.mock.calls[0][1]).toEqual(expect.objectContaining({
      expectedVersion: 3, state: 'AwaitingRecipientAcceptance', stage: 'Create', generation: 0,
    }))
    expect(api.acceptNftLifecycle).not.toHaveBeenCalled()
    cleanup()
    render(<NftLifecyclePanel value={{ ...lapsed, actorUserId: 'issuer', recipientUserId: 'user' }} onChange={vi.fn()} />)
    expect(screen.getByText("The requester's authority has lapsed; this stage continues after they renew it.")).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Renew my authority' })).not.toBeInTheDocument()
  })
  it('renews a lapsed participant acceptance with a fresh saved request instead of replaying the original', async () => {
    const original = { requestId: 'original', expectedVersion: 7, state: 'AwaitingRecipientAcceptance' as const, stage: 'Create', generation: 0 }
    const lapsed = { ...operation, state: 'Preparing' as const, stage: 'Accept', version: 9, stageAcceptance: original,
      needsParticipantRenewal: true, renewalParticipantUserId: 'user' }
    api.acceptNftLifecycle.mockImplementationOnce(async (_id: string, request: { requestId: string }) => {
      expect(JSON.parse(localStorage.getItem('azoa:nft-participant-renewal:user:opaque:9')!)).toEqual(request)
      return { ok: true, value: { ...lapsed, needsParticipantRenewal: false, state: 'Submitted' } }
    })
    render(<NftLifecyclePanel value={lapsed} onChange={vi.fn()} />)
    fireEvent.click(screen.getByRole('button', { name: 'Renew my acceptance' }))
    await waitFor(() => expect(api.acceptNftLifecycle).toHaveBeenCalledTimes(1))
    const sent = api.acceptNftLifecycle.mock.calls[0][1]
    expect(sent.requestId).not.toBe('original')
    expect(sent).toEqual(expect.objectContaining({ expectedVersion: 9, state: 'Preparing', stage: 'Accept', generation: 0 }))
  })
  it('rejects a malformed typed operation reference without calling the server', async () => {
    render(<NftLifecyclePanel value={null} onChange={vi.fn()} />)
    fireEvent.change(screen.getByLabelText('Resume an operation'), { target: { value: '../other-operation' } })
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }))
    await screen.findByText('Enter the 32-character lowercase operation reference.')
    expect(api.refreshNftLifecycle).not.toHaveBeenCalled()
  })
})
