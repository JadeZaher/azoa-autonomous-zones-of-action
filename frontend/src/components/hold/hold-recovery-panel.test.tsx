import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { HoldRecoveryPanel } from './hold-recovery-panel'

const api = vi.hoisted(() => ({ listHoldCommands: vi.fn(), acceptHoldOpening: vi.fn(), reconcileHoldOpening: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
const row = { operationId: 'operation', version: 3, kind: 'Open', state: 'AwaitingConsent', holdId: 'work',
  actorUserId: 'actor', beneficiaryUserId: 'actor', quantityBaseUnits: '4', asset: { assetId: '7', chainType: 'Algorand', network: 'Testnet' }, mandateDraftHash: 'draft' }
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('persists acceptance before HTTP and retains exact unresolved request after browser reload', async () => {
  api.listHoldCommands.mockResolvedValue({ ok: true, value: { items: [row], nextOffset: null } })
  api.acceptHoldOpening.mockImplementation(async (id: string, request: unknown) => {
    expect(JSON.parse(localStorage.getItem('azoa.holdRecovery.v1.actor')!)[0]).toEqual({ operationId: id, kind: 'WorkflowOpeningAccept', request })
    throw new Error('response lost')
  })
  const view = render(<HoldRecoveryPanel actorId="actor" scope="actor" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Accept opening and release limits' }))
  await screen.findByText('response lost')
  const original = api.acceptHoldOpening.mock.calls[0][1]
  view.unmount()
  api.reconcileHoldOpening.mockResolvedValue({ ok: true, value: { state: 'Unresolved', command: row } })
  render(<HoldRecoveryPanel actorId="actor" scope="actor" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Reconcile saved WorkflowOpeningAccept' }))
  await waitFor(() => expect(api.reconcileHoldOpening).toHaveBeenCalledWith('operation', original))
  expect(JSON.parse(localStorage.getItem('azoa.holdRecovery.v1.actor')!)[0].request).toEqual(original)
  expect(api.acceptHoldOpening).toHaveBeenCalledTimes(1)
})

it('ignores late discovery after changing authenticated scope', async () => {
  let finish!: (value: unknown) => void
  api.listHoldCommands.mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
    .mockResolvedValueOnce({ ok: true, value: { items: [], nextOffset: null } })
  const view = render(<HoldRecoveryPanel actorId="old" scope="old" />)
  view.rerender(<HoldRecoveryPanel actorId="new" scope="new" />)
  await act(async () => finish({ ok: true, value: { items: [row], nextOffset: null } }))
  expect(screen.queryByText('work: Open - AwaitingConsent')).not.toBeInTheDocument()
  expect(localStorage.getItem('azoa.holdRecovery.v1.new')).toBeNull()
})
