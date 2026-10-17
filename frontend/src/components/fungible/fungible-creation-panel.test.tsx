import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { FungibleCreationPanel } from './fungible-creation-panel'
const api = vi.hoisted(() => ({ listFungibleCreations: vi.fn(), fungibleMint: vi.fn(), continueFungibleCreation: vi.fn(), getFungibleCreation: vi.fn(), getFungibleAttachment: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (result: { ok: boolean }) => result.ok }))
const view = { operationId: 'saved-operation', version: 2, state: 'AwaitingConsent', chainType: 'Algorand', network: 'Devnet', purpose: 'Participant', walletAddress: 'reserve', walletId: 'wallet', walletProvisioned: false }
beforeEach(() => { localStorage.clear(); vi.resetAllMocks(); api.listFungibleCreations.mockResolvedValue({ ok: true, value: [] }) })
it('persists the original descriptor before admission and replays it after lost response', async () => {
  api.fungibleMint.mockResolvedValueOnce({ ok: false, error: { message: 'response lost' } }).mockResolvedValueOnce({ ok: true, value: { operation: view } })
  render(<FungibleCreationPanel actorId="actor" />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Admit a new distinct issuance' })).not.toBeDisabled())
  fireEvent.change(screen.getByLabelText('Supply in atomic units (exact integer)'), { target: { value: '18446744073709551615' } })
  fireEvent.click(screen.getByRole('button', { name: 'Admit a new distinct issuance' }))
  await screen.findByText('response lost')
  expect(JSON.parse(localStorage.getItem('azoa:fungible-creation:v1:actor')!).requests[0].params.total).toBe('18446744073709551615')
  fireEvent.click(screen.getByRole('button', { name: 'Replay original request' }))
  await screen.findByText(/AwaitingConsent - Algorand/)
  expect(api.fungibleMint.mock.calls[1]).toEqual(api.fungibleMint.mock.calls[0])
  expect(screen.queryByText(/Finalized asset:/)).not.toBeInTheDocument()
})
it('server discovery recovers operations after browser storage is cleared without signing', async () => {
  api.listFungibleCreations.mockResolvedValue({ ok: true, value: [view] })
  render(<FungibleCreationPanel actorId="actor" />)
  await screen.findByText(/AwaitingConsent - Algorand/)
  expect(api.continueFungibleCreation).not.toHaveBeenCalled()
  expect(api.fungibleMint).not.toHaveBeenCalled()
})

it.each(['continue', 'replace', 'cancel'] as const)('retires saved %s only after a later server version fences its CAS', async kind => {
  const command = { operationId: view.operationId, version: 2, requestId: 'original-command', kind }
  localStorage.setItem('azoa:fungible-creation:v1:actor', JSON.stringify({ requests: [], commands: [command] }))
  api.listFungibleCreations.mockResolvedValue({ ok: true, value: [view] })
  api.getFungibleCreation.mockResolvedValueOnce({ ok: true, value: view })
    .mockResolvedValueOnce({ ok: false, error: { message: 'read unavailable' } })
    .mockResolvedValueOnce({ ok: true, value: { ...view, version: 3 } })
  render(<FungibleCreationPanel actorId="actor" />)
  const reconcile = await screen.findByRole('button', { name: 'Reconcile saved command with server' })
  fireEvent.click(reconcile)
  await screen.findByText(/server has not fenced this saved version/)
  expect(JSON.parse(localStorage.getItem('azoa:fungible-creation:v1:actor')!).commands).toEqual([command])
  fireEvent.click(reconcile)
  await screen.findByText('read unavailable')
  expect(JSON.parse(localStorage.getItem('azoa:fungible-creation:v1:actor')!).commands).toEqual([command])
  fireEvent.click(reconcile)
  await waitFor(() => expect(JSON.parse(localStorage.getItem('azoa:fungible-creation:v1:actor')!).commands).toEqual([]))
  expect(JSON.parse(localStorage.getItem('azoa:fungible-creation:v1:actor:resolved-command:original-command')!).command).toEqual(command)
  expect(api.continueFungibleCreation).not.toHaveBeenCalled()
})

it('reconciles an attachment only against the same finalized projection and later version', async () => {
  const attachment = { ...view, state: 'AttachmentPending', projectionAuthorityId: 'authority', holonId: 'holon', assetId: '123' }
  const pending = { requestId: 'attachment-request', version: 2, projectionAuthorityId: 'authority' }
  const key = `azoa:fungible-creation:v1:actor:attachment:${view.operationId}`
  localStorage.setItem(key, JSON.stringify(pending))
  api.getFungibleAttachment.mockResolvedValueOnce({ ok: true, value: attachment })
    .mockResolvedValueOnce({ ok: true, value: { ...attachment, version: 3, projectionAuthorityId: 'other' } })
    .mockResolvedValueOnce({ ok: true, value: { ...attachment, version: 3 } })
  render(<FungibleCreationPanel actorId="actor" />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Admit a new distinct issuance' })).not.toBeDisabled())
  fireEvent.change(screen.getByLabelText('Attachment operation ID'), { target: { value: view.operationId } })
  fireEvent.click(screen.getByRole('button', { name: 'Inspect authorized attachment' }))
  const reconcile = await screen.findByRole('button', { name: 'Reconcile saved attachment with server' })
  fireEvent.click(reconcile)
  await screen.findByText(/server has not fenced this saved attachment version/)
  expect(JSON.parse(localStorage.getItem(key)!)).toEqual(pending)
  fireEvent.click(reconcile)
  await waitFor(() => expect(localStorage.getItem(key)).toBeNull())
  expect(JSON.parse(localStorage.getItem(`${key}:resolved:attachment-request`)!).command).toEqual(pending)
})
