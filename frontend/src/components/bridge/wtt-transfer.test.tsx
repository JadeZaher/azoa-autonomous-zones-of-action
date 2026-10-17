import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { WttTransferPanel } from './wtt-transfer'

const api = vi.hoisted(() => ({
  findWttByRequestKey: vi.fn(), startWtt: vi.fn(), cancelWtt: vi.fn(), refreshWtt: vi.fn(), continueWtt: vi.fn(),
  exposeWttParticipant: vi.fn(), acceptWttParticipantSignature: vi.fn(),
}))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (result: { ok: boolean }) => result.ok }))
vi.mock('@/lib/azoa-auth', () => ({ useAzoaAuth: () => ({ userId: 'user-1' }) }))
vi.mock('@/lib/azoa-hooks', () => ({ useWallets: () => ({ wallets: [] }) }))

const saved = { requestKey: 'saved-key', offerId: 'offer', offerHash: 'h'.repeat(64), operationId: 'o'.repeat(64) }
const operation = (patch: Record<string, unknown>) => ({
  operationId: 'o'.repeat(64), offerId: 'offer', version: 4, state: 'Active', publicState: 'TargetSubmitted', phase: 'Target',
  stages: [{ stageId: 's'.repeat(64), phase: 'Target', stage: 'Redeem', generation: 0, version: 2, state: 'SubmissionAuthorized',
    transactionId: 'redeem-tx', submissionAuthorized: true, exposed: false }],
  currentStageId: 's'.repeat(64), proofVerified: true, nextAction: 'AwaitFinality', transferSequence: '42', settlement: null, ...patch,
})

beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('replays the identical saved start after a lost response and can fence it by request key', async () => {
  localStorage.setItem('azoa:wtt-transfer:user-1', JSON.stringify(saved))
  api.findWttByRequestKey.mockResolvedValue({ ok: false, error: { message: 'not found' } })
  api.startWtt.mockResolvedValue({ ok: false, error: { message: 'Response lost' } })
  api.cancelWtt.mockResolvedValue({ ok: true, value: operation({ state: 'Cancelled', publicState: 'CancelledBeforeEffect', phase: 'Source', stages: [], currentStageId: null, nextAction: 'None' }) })
  render(<WttTransferPanel />)
  await waitFor(() => expect(api.findWttByRequestKey).toHaveBeenCalledWith('saved-key'))
  fireEvent.click(await screen.findByRole('button', { name: 'Retry saved start' }))
  await waitFor(() => expect(api.startWtt).toHaveBeenCalledTimes(1))
  fireEvent.click(screen.getByRole('button', { name: 'Retry saved start' }))
  await waitFor(() => expect(api.startWtt).toHaveBeenCalledTimes(2))
  expect(api.startWtt.mock.calls[1]).toEqual(api.startWtt.mock.calls[0])
  expect(api.startWtt.mock.calls[0][1]).toBe('saved-key')
  fireEvent.click(screen.getByRole('button', { name: 'Cancel saved request' }))
  await waitFor(() => expect(api.cancelWtt).toHaveBeenCalledWith({ offerId: 'offer', offerHash: saved.offerHash }, 'saved-key'))
  expect(await screen.findByText('CancelledBeforeEffect')).toBeInTheDocument()
})

it('shows a submitted redemption as pending until a refresh returns finalized settlement', async () => {
  localStorage.setItem('azoa:wtt-transfer:user-1', JSON.stringify(saved))
  api.findWttByRequestKey.mockResolvedValue({ ok: true, value: operation({}) })
  api.refreshWtt.mockResolvedValue({ ok: true, value: operation({ state: 'Settled', publicState: 'Settled', phase: 'Complete', nextAction: 'None',
    settlement: { version: 1, targetTransactionId: 'redeem-tx', targetProtocolIdentity: 'solana:x', bodyDigest: 'd', recipientAddress: 'r',
      recipientAmount: '1000', redemptionEvidenceJson: '{}' } }) })
  render(<WttTransferPanel />)
  expect(await screen.findByText('TargetSubmitted')).toBeInTheDocument()
  expect(screen.queryByText('Redemption transaction')).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Sign and send next stage' })).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
  expect(await screen.findByText('Settled')).toBeInTheDocument()
  expect(screen.getByText('Redemption transaction')).toBeInTheDocument()
})
