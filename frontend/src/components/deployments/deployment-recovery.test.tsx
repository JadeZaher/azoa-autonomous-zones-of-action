import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import type { DeploymentReceipt } from 'azoa-sdk'
import { DeploymentRecovery } from './deployment-recovery'

const api = vi.hoisted(() => ({ recoverDeploymentWithinBudget: vi.fn(), quoteDeploymentRecovery: vi.fn(), startDeploymentRecovery: vi.fn(), getDeployment: vi.fn(), invalidateDeploymentRecovery: vi.fn(), inspectDeploymentRecoveryAncestry: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (result: { ok: boolean }) => result.ok }))
vi.mock('@/lib/azoa-context', () => ({ useAzoa: () => ({ wallets: [] }) }))
const receipt: DeploymentReceipt = { operationId: 'original', artifactId: 'artifact', artifactHash: 'hash', target: { kind: 'NativeContract', chainType: 'Solana', network: 'Devnet' }, state: 'Failed', stage: 'Write:0', transactionIds: [], version: 7 }
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('inspects recovery history in explicit pages without authorizing a replacement', async () => {
  api.inspectDeploymentRecoveryAncestry.mockResolvedValueOnce({ ok: true, value: { headOperationId: 'child', state: 'Running', inspectedCount: 25, digest: 'page-one' } })
    .mockResolvedValueOnce({ ok: true, value: { headOperationId: 'child', state: 'Complete', inspectedCount: 30, digest: 'complete' } })
  render(<DeploymentRecovery userId="actor" receipt={{ ...receipt, recovery: { mode: 'AbortCleanup', childOperationId: 'child', projected: false } }} onReceipt={vi.fn()} />)
  const inspect = screen.getByRole('button', { name: 'Inspect next recovery history page' })
  fireEvent.click(inspect)
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('25'))
  await waitFor(() => expect(inspect).toBeEnabled())
  fireEvent.click(inspect)
  await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Complete'))
  expect(inspect).toBeDisabled()
  expect(api.startDeploymentRecovery).not.toHaveBeenCalled()
  expect(api.recoverDeploymentWithinBudget).not.toHaveBeenCalled()
})

it.each([7, 8])('reconciles a saved original request only when the server version proves its CAS cannot admit (%i)', async version => {
  const saved = { kind: 'original', request: { expectedVersion: 7, mode: 'AbortCleanup', acknowledgeRecovery: true } }
  localStorage.setItem('azoa:deployment-recovery:actor:original', JSON.stringify(saved))
  api.getDeployment.mockResolvedValue({ ok: true, value: { ...receipt, version, recovery: { mode: 'None', projected: false } } })
  render(<DeploymentRecovery userId="actor" receipt={receipt} onReceipt={vi.fn()} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Check saved request status' }))
  await waitFor(() => expect(api.getDeployment).toHaveBeenCalledWith('original'))
  if (version > 7) {
    await screen.findByRole('button', { name: 'Recover within original limits' })
    expect(localStorage.getItem('azoa:deployment-recovery:actor:original')).toBeNull()
    expect(screen.getByRole('checkbox')).not.toBeChecked()
  } else {
    await screen.findByRole('alert')
    expect(localStorage.getItem('azoa:deployment-recovery:actor:original')).not.toBeNull()
  }
  expect(api.recoverDeploymentWithinBudget).not.toHaveBeenCalled()
})

it('persists consent before send and replays the same original-budget command after remount', async () => {
  api.recoverDeploymentWithinBudget.mockImplementation(async () => {
    expect(localStorage.getItem('azoa:deployment-recovery:actor:original')).not.toBeNull()
    throw new Error('Response lost')
  })
  const first = render(<DeploymentRecovery userId="actor" receipt={receipt} onReceipt={vi.fn()} />)
  fireEvent.click(screen.getByRole('checkbox'))
  fireEvent.click(screen.getByRole('button', { name: 'Recover within original limits' }))
  await screen.findByRole('button', { name: 'Retry saved recovery request' })
  const originalCall = api.recoverDeploymentWithinBudget.mock.calls[0]
  first.unmount()
  render(<DeploymentRecovery userId="actor" receipt={{ ...receipt, version: 8 }} onReceipt={vi.fn()} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Retry saved recovery request' }))
  await waitFor(() => expect(api.recoverDeploymentWithinBudget).toHaveBeenCalledTimes(2))
  expect(api.recoverDeploymentWithinBudget.mock.calls[1]).toEqual(originalCall)
})

it('allows a new quote only after the server reports the original cap is insufficient', async () => {
  api.recoverDeploymentWithinBudget.mockResolvedValue({ ok: true, value: { ...receipt, state: 'RecoveryQuoteRequired' } })
  render(<DeploymentRecovery userId="actor" receipt={receipt} onReceipt={vi.fn()} />)
  fireEvent.click(screen.getByRole('checkbox'))
  fireEvent.click(screen.getByRole('button', { name: 'Recover within original limits' }))
  await waitFor(() => expect(localStorage.getItem('azoa:deployment-recovery:actor:original')).toBeNull())
  expect(screen.getByRole('checkbox')).not.toBeChecked()
  expect(api.startDeploymentRecovery).not.toHaveBeenCalled()
})

it('does not send if the exact recovery request cannot be persisted', async () => {
  render(<DeploymentRecovery userId="actor" receipt={receipt} onReceipt={vi.fn()} />)
  const fail = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage unavailable') })
  fireEvent.click(screen.getByRole('checkbox'))
  fireEvent.click(screen.getByRole('button', { name: 'Recover within original limits' }))
  await screen.findByRole('alert')
  expect(api.recoverDeploymentWithinBudget).not.toHaveBeenCalled()
  fail.mockRestore()
})

it.each(['camel', 'pascal'])('shows the saved linked mode and economic limits (%s) and retries exact authorization', async casing => {
  const request = { requestKey: 'saved-key', acknowledgeCosts: true, quote: { parentVersion: 7, bindingHash: 'binding', intent: {
    version: 1, operationKey: 'child', kind: 'Recovery', chainType: 'Solana', network: 'Devnet', protocolIdentity: 'loader', artifactHash: 'hash',
    input: casing === 'camel' ? { mode: 'FinalizeAuthority', payerAddress: 'saved-payer' } : { Mode: 'FinalizeAuthority', PayerAddress: 'saved-payer' }, authorities: [],
    budget: { maximumNetworkFee: '25000', maximumLockedRent: '0', maximumValue: '0', quoteId: 'quote' },
    quote: { quoteId: 'quote', intentHash: 'intent', protocolIdentity: 'loader', networkFee: '5000', lockedRent: '0', value: '0', planJson: '{}', expiresAt: '2026-10-15T00:00:00Z', disclosure: { refundDestination: 'saved-return' } },
  } } }
  localStorage.setItem('azoa:deployment-recovery:actor:original', JSON.stringify({ kind: 'linked', request }))
  api.startDeploymentRecovery.mockRejectedValue(new Error('Response lost'))
  render(<DeploymentRecovery userId="actor" receipt={receipt} onReceipt={vi.fn()} />)
  expect(await screen.findByLabelText('Recovery action')).toHaveValue('FinalizeAuthority')
  expect(screen.getByLabelText('Saved recovery authorization')).toHaveTextContent('25000')
  expect(screen.getByLabelText('Saved recovery authorization')).toHaveTextContent('saved-payer')
  expect(screen.getByLabelText('Saved recovery authorization')).toHaveTextContent('saved-return')
  fireEvent.click(screen.getByRole('button', { name: 'Retry saved recovery request' }))
  await waitFor(() => expect(api.startDeploymentRecovery).toHaveBeenCalledWith('original', request))
  expect(api.quoteDeploymentRecovery).not.toHaveBeenCalled()
  expect(JSON.parse(localStorage.getItem('azoa:deployment-recovery:actor:original')!).request).toEqual(request)
  api.invalidateDeploymentRecovery.mockRejectedValueOnce(new Error('Withdrawal response lost'))
  const withdraw = screen.getByRole('button', { name: 'Withdraw unadmitted recovery quote' })
  await waitFor(() => expect(withdraw).toBeEnabled())
  fireEvent.click(withdraw)
  await screen.findByText('Withdrawal response lost')
  expect(JSON.parse(localStorage.getItem('azoa:deployment-recovery:actor:original')!).request).toEqual(request)
  api.invalidateDeploymentRecovery.mockResolvedValueOnce({ ok: true, value: { ...receipt, version: 8 } })
  fireEvent.click(withdraw)
  await screen.findByRole('button', { name: 'Recover within original limits' })
  expect(api.invalidateDeploymentRecovery.mock.calls.slice(-2)).toEqual([['original', request], ['original', request]])
  expect(localStorage.getItem('azoa:deployment-recovery:actor:original')).toBeNull()
  expect(screen.getByRole('checkbox')).not.toBeChecked()
})
