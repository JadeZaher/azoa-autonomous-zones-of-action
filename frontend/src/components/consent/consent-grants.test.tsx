import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { ConsentGrantsPanel } from './consent-grants'

const api = vi.hoisted(() => ({ listConsentGrants: vi.fn(), grantConsent: vi.fn(), withdrawConsentCommand: vi.fn(), revokeConsent: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api } }))
const owner = '11111111-1111-1111-1111-111111111111'
const tenant = '22222222-2222-2222-2222-222222222222'
const grantId = '33333333-3333-3333-3333-333333333333'
const key = `azoa:consent-grant:${owner}`
beforeEach(() => {
  localStorage.clear(); vi.resetAllMocks()
  api.listConsentGrants.mockResolvedValue({ ok: true, value: [] })
})

it('persists before submission and recovers the original command without treating its receipt as current permission', async () => {
  api.grantConsent.mockImplementationOnce(async command => {
    expect(JSON.parse(localStorage.getItem(key)!)).toEqual(command)
    return { ok: false, error: { message: 'Response lost' } }
  }).mockImplementationOnce(async command => ({ ok: true, value: { ...command, grantId, grantorUserId: owner, grantedAt: '2026-10-15T00:00:00Z' } }))
  render(<ConsentGrantsPanel userId={owner} />)
  fireEvent.change(screen.getByLabelText('Tenant ID for consent'), { target: { value: tenant } })
  fireEvent.change(screen.getByLabelText('Consent scopes'), { target: { value: 'kyc:read' } })
  fireEvent.click(screen.getByRole('button', { name: 'Grant selected permissions' }))
  await screen.findByText('Response lost')
  api.listConsentGrants.mockResolvedValue({ ok: true, value: [{ grantId, tenantId: tenant, scopes: ['kyc:read'], revokedAt: '2026-10-16T00:00:00Z' }] })
  fireEvent.click(screen.getByRole('button', { name: 'Recover saved consent' }))
  await screen.findByText('Revoked 2026-10-16T00:00:00Z')
  expect(api.grantConsent.mock.calls[0]).toEqual(api.grantConsent.mock.calls[1])
  expect(localStorage.getItem(key)).toBeNull()
  expect(screen.queryByRole('button', { name: 'Revoke permissions' })).toBeNull()
})

it('retains a command when a response claims different permissions', async () => {
  const command = { requestId: owner, tenantId: tenant, scopes: ['kyc:read'], origin: 'UserExplicit', expiresAt: null }
  localStorage.setItem(key, JSON.stringify(command))
  api.grantConsent.mockResolvedValue({ ok: true, value: { ...command, grantId, grantorUserId: owner, scopes: ['transfer:sign'] } })
  render(<ConsentGrantsPanel userId={owner} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Recover saved consent' }))
  await screen.findByText('Consent receipt did not match the saved request.')
  expect(JSON.parse(localStorage.getItem(key)!)).toEqual(command)
})

it('ignores a stale list response and repeats a lost revocation against the same grant', async () => {
  let initial!: (value: unknown) => void
  const row = { grantId, tenantId: tenant, scopes: ['kyc:read'], revokedAt: null }
  api.listConsentGrants.mockImplementationOnce(() => new Promise(resolve => { initial = resolve }))
    .mockResolvedValue({ ok: true, value: [row] })
  api.revokeConsent.mockResolvedValueOnce({ ok: false, error: { message: 'Revocation response lost' } })
    .mockResolvedValueOnce({ ok: true, value: { grantId, revokedAt: '2026-10-16T00:00:00Z', notificationRecorded: true } })
  render(<ConsentGrantsPanel userId={owner} />)
  fireEvent.click(screen.getByRole('button', { name: 'Refresh permissions' }))
  await screen.findByRole('button', { name: 'Revoke permissions' })
  await act(async () => { initial({ ok: true, value: [] }) })
  fireEvent.click(screen.getByRole('button', { name: 'Revoke permissions' }))
  await screen.findByText('Revocation response lost')
  fireEvent.click(screen.getByRole('button', { name: 'Revoke permissions' }))
  await waitFor(() => expect(api.revokeConsent).toHaveBeenCalledTimes(2))
  expect(api.revokeConsent.mock.calls).toEqual([[grantId], [grantId]])
})

it('withdraws a rejected saved command only after authoritative confirmation', async () => {
  const command = { requestId: owner, tenantId: tenant, scopes: ['bogus:scope'], origin: 'UserExplicit', expiresAt: null }
  localStorage.setItem(key, JSON.stringify(command))
  api.withdrawConsentCommand.mockResolvedValueOnce({ ok: false, error: { message: 'Withdrawal response lost' } })
    .mockResolvedValueOnce({ ok: true, value: { requestId: owner, withdrawn: true, receipt: null } })
  render(<ConsentGrantsPanel userId={owner} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Withdraw saved request' }))
  await screen.findByText('Withdrawal response lost')
  expect(JSON.parse(localStorage.getItem(key)!)).toEqual(command)
  fireEvent.click(screen.getByRole('button', { name: 'Withdraw saved request' }))
  await waitFor(() => expect(localStorage.getItem(key)).toBeNull())
  expect(api.withdrawConsentCommand.mock.calls).toEqual([[command], [command]])
  expect(JSON.parse(localStorage.getItem(`${key}:withdrawn:${owner}`)!).command).toEqual(command)
})

it('recovers the grant when admission won the withdrawal race', async () => {
  const command = { requestId: owner, tenantId: tenant, scopes: ['kyc:read'], origin: 'UserExplicit', expiresAt: null }
  localStorage.setItem(key, JSON.stringify(command))
  const receipt = { ...command, grantId, grantorUserId: owner, grantedAt: '2026-10-15T00:00:00Z' }
  api.withdrawConsentCommand.mockResolvedValue({ ok: true, value: { requestId: owner, withdrawn: false, receipt } })
  api.listConsentGrants.mockResolvedValue({ ok: true, value: [receipt] })
  render(<ConsentGrantsPanel userId={owner} />)
  fireEvent.click(await screen.findByRole('button', { name: 'Withdraw saved request' }))
  await screen.findByText('The request already created a grant. Review its current status below and revoke it if needed.')
  expect(localStorage.getItem(`${key}:withdrawn:${owner}`)).toBeNull()
  expect(screen.getByRole('button', { name: 'Revoke permissions' })).toBeTruthy()
})
