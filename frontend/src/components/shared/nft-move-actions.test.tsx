import { act, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import { NftMoveActions } from './nft-move-actions'

const api = vi.hoisted(() => ({ transferUserNFT: vi.fn(), burnUserNFT: vi.fn(), getNftFeeQuote: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
vi.mock('@/lib/azoa-auth', () => ({ useAzoaAuth: () => ({ userId: 'user' }) }))
beforeEach(() => { localStorage.clear(); vi.clearAllMocks() })

it('reuses the persisted transfer key and recipient after a browser reload', async () => {
  localStorage.setItem('azoa:nft-move:user:nft', JSON.stringify({ kind: 'transfer', key: 'original-key', recipient: 'original-recipient' }))
  api.transferUserNFT.mockResolvedValue({ ok: false, error: { message: 'Connection interrupted' } })
  const first = render(<NftMoveActions nftId="nft" chainType="Algorand" transferable onStarted={vi.fn()} />)
  await waitFor(() => expect(screen.getByLabelText('Recipient address')).toHaveValue('original-recipient'))
  fireEvent.click(screen.getByRole('button', { name: 'Retry transfer' }))
  await waitFor(() => expect(api.transferUserNFT).toHaveBeenCalledWith('nft', 'original-recipient', 'original-key', undefined))
  first.unmount()
  render(<NftMoveActions nftId="nft" chainType="Algorand" transferable onStarted={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: 'Retry transfer' }))
  await waitFor(() => expect(api.transferUserNFT).toHaveBeenCalledTimes(2))
  expect(api.transferUserNFT.mock.calls[1]).toEqual(['nft', 'original-recipient', 'original-key', undefined])
})

it('does not replace an unreadable pending request with a fresh burn', async () => {
  localStorage.setItem('azoa:nft-move:user:nft', '{broken')
  render(<NftMoveActions nftId="nft" chainType="Algorand" transferable onStarted={vi.fn()} />)
  await waitFor(() => expect(screen.getByRole('button', { name: 'Review burn fee' })).toBeDisabled())
  expect(api.burnUserNFT).not.toHaveBeenCalled()
  expect(localStorage.getItem('azoa:nft-move:user:nft')).toBe('{broken')
})

it('shows the quoted fee before a burn can be authorized', async () => {
  api.getNftFeeQuote.mockResolvedValue({ ok: true, value: { payerUserId: 'user', operation: 'Burn', chainType: 'Algorand', network: 'testnet', currency: 'ALGO', decimals: 6, nativeBaseUnits: '1250000', revision: 7, quoteId: "quote-seven" } })
  api.burnUserNFT.mockResolvedValue({ ok: true, value: { operationId: 'op', state: 'Submitted' } })
  render(<NftMoveActions nftId="nft" chainType="Algorand" transferable onStarted={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: 'Review burn fee' }))
  await waitFor(() => expect(screen.getByText('1.250000 ALGO')).toBeInTheDocument())
  expect(api.burnUserNFT).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Authorize fee and burn' }))
  await waitFor(() => expect(api.burnUserNFT).toHaveBeenCalledWith('nft', expect.any(String), { revision: 7, quoteId: "quote-seven", maxNativeBaseUnits: '1250000' }))
})

it('discards a quote returned after the asset chain changes', async () => {
  let resolve!: (value: unknown) => void
  api.getNftFeeQuote.mockReturnValueOnce(new Promise(done => { resolve = done }))
  const view = render(<NftMoveActions nftId="first" chainType="Algorand" transferable onStarted={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: 'Review burn fee' }))
  view.rerender(<NftMoveActions nftId="second" chainType="Solana" transferable onStarted={vi.fn()} />)
  await act(async () => resolve({ ok: true, value: { payerUserId: 'user', operation: 'Burn', chainType: 'Algorand', network: 'testnet', currency: 'ALGO', decimals: 6, nativeBaseUnits: '1250000', revision: 7, quoteId: "quote-seven" } }))
  expect(screen.queryByText('1.250000 ALGO')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Review burn fee' })).toBeEnabled()
  expect(api.burnUserNFT).not.toHaveBeenCalled()
})
