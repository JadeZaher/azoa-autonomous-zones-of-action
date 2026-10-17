import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { HoldMandatePanel } from './hold-mandate-panel'

const api = vi.hoisted(() => ({ listHoldReleaseMandates: vi.fn(), revokeHoldReleaseMandate: vi.fn(), reconcileHoldReleaseMandate: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
const costs = { payerAddress: 'reserve', refundRecipientAddress: 'reserve', networkFeeBaseUnits: '2000', lockedBalanceBaseUnits: '0', nonrefundableProtocolCostBaseUnits: '0' }
const mandate = { id: 'mandate', version: 2, status: 'Active', terms: { version: 1, holdRecordId: 'liability', definitionHash: 'definition',
  asset: { chainType: 'Algorand', network: 'Testnet', protocolIdentity: 'genesis', assetId: '42', holdingAddress: 'reserve' }, quantityBaseUnits: '400',
  recipient: { id: 'wallet', ownerUserId: 'owner', address: 'recipient', chainType: 'Algorand' }, notBefore: '2026-11-01T00:00:00Z',
  expiresAt: '2026-12-01T00:00:00Z', nativeCurrency: 'ALGO', nativeDecimals: 6, maximumCosts: costs } }
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('journals a revoke before HTTP, keeps it across reload after a lost response, and archives it once reconciled', async () => {
  api.listHoldReleaseMandates.mockResolvedValue({ ok: true, value: { items: [mandate], nextOffset: null } })
  api.revokeHoldReleaseMandate.mockImplementation(async (id: string, request: unknown) => {
    expect(JSON.parse(localStorage.getItem('azoa.holdMandates.v1.owner')!)[0]).toEqual({ mandateId: id, kind: 'MandateRevoke', request })
    throw new Error('response lost')
  })
  const view = render(<HoldMandatePanel scope="owner" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Revoke automatic release' }))
  await screen.findByText('response lost')
  const original = api.revokeHoldReleaseMandate.mock.calls[0][1]
  expect(original).toMatchObject({ version: 2 })
  view.unmount()

  const revoked = { ...mandate, version: 3, status: 'Revoked' }
  api.reconcileHoldReleaseMandate.mockResolvedValue({ ok: true, value: { state: 'Applied', mandate: revoked } })
  render(<HoldMandatePanel scope="owner" />)
  fireEvent.click(await screen.findByRole('button', { name: 'Reconcile saved MandateRevoke' }))

  await waitFor(() => expect(api.reconcileHoldReleaseMandate).toHaveBeenCalledWith('mandate', { kind: 'MandateRevoke', request: original }))
  await screen.findByText('400 base units of 42 - Revoked')
  expect(JSON.parse(localStorage.getItem('azoa.holdMandates.v1.owner')!)).toEqual([])
  expect(screen.queryByRole('button', { name: 'Revoke automatic release' })).not.toBeInTheDocument()
  expect(api.revokeHoldReleaseMandate).toHaveBeenCalledTimes(1)
})
