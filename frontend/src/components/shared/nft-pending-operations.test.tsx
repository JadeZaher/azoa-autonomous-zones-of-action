import { act, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import { NftPendingOperations } from './nft-pending-operations'

const api = vi.hoisted(() => ({ listNftLifecycles: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
it('discovers recipient work from the server and follows an empty filtered page', async () => {
  localStorage.clear()
  const operation = { operationId: 'issued-on-another-device', operation: 'Mint', state: 'AwaitingRecipientAcceptance', nftId: 'nft' }
  api.listNftLifecycles.mockResolvedValueOnce({ ok: true, value: { items: [], nextCursor: 'next' } })
    .mockResolvedValueOnce({ ok: true, value: { items: [operation], nextCursor: null } })
  const onSelect = vi.fn()
  render(<NftPendingOperations userId="recipient" onSelect={onSelect} />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Load more' })).toBeInTheDocument())
  fireEvent.click(screen.getByRole('button', { name: 'Load more' }))
  await waitFor(() => expect(api.listNftLifecycles).toHaveBeenLastCalledWith('next'))
  fireEvent.click(await screen.findByRole('button', { name: /AwaitingRecipientAcceptance/ }))
  expect(onSelect).toHaveBeenCalledWith(operation)
})

it('never publishes a late previous-account result into the next account', async () => {
  let resolve!: (value: unknown) => void
  api.listNftLifecycles.mockReturnValueOnce(new Promise(done => { resolve = done }))
    .mockResolvedValueOnce({ ok: true, value: { items: [], nextCursor: null } })
  const view = render(<NftPendingOperations userId="previous" onSelect={vi.fn()} />)
  view.rerender(<NftPendingOperations userId="next" onSelect={vi.fn()} />)
  await act(async () => resolve({ ok: true, value: { items: [{ operationId: 'private', operation: 'Mint', state: 'Submitted', nftId: 'previous-account-asset' }], nextCursor: 'private-cursor' } }))
  expect(screen.queryByText(/previous-account-asset/)).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
})
