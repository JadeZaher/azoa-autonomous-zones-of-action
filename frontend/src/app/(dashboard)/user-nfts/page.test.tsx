import { act, render, screen, fireEvent, waitFor } from '@testing-library/react'
import { beforeEach, expect, it, vi } from 'vitest'
import UserNftsPage from './page'
const api = vi.hoisted(() => ({ getNftFeeQuote: vi.fn(), quoteUserNftMintCosts: vi.fn(), mintUserNFT: vi.fn(), request: vi.fn(), listNftLifecycles: vi.fn() }))
vi.mock('@/lib/azoa', () => ({ azoa: { api }, isOk: (value: { ok: boolean }) => value.ok }))
vi.mock('@/lib/azoa-auth', () => ({ useAzoaAuth: () => ({ userId: 'user' }) }))
beforeEach(() => {
  localStorage.clear(); vi.clearAllMocks()
  api.request.mockResolvedValue({ ok: true, value: [] })
  api.listNftLifecycles.mockResolvedValue({ ok: true, value: { items: [], nextCursor: null } })
})
it('recovers mint fee review after a chain switch while the previous quote is in flight', async () => {
  let resolve!: (value: unknown) => void
  api.getNftFeeQuote.mockReturnValueOnce(new Promise(done => { resolve = done }))
  render(<UserNftsPage />)
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'NFT' } })
  fireEvent.change(screen.getByLabelText('Metadata URI'), { target: { value: 'ipfs://metadata' } })
  fireEvent.submit(screen.getByLabelText('Name').closest('form')!)
  await waitFor(() => expect(api.getNftFeeQuote).toHaveBeenCalledWith('Algorand', 'Mint'))
  fireEvent.click(screen.getByLabelText('Chain'))
  fireEvent.click(await screen.findByRole('option', { name: 'Solana' }))
  await act(async () => resolve({ ok: true, value: { chainType: 'Algorand', currency: 'ALGO', decimals: 6, nativeBaseUnits: '1000000', quoteId: 'old', revision: 1 } }))
  expect(screen.queryByText('1.000000 ALGO')).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Review mint fee' })).toBeEnabled()
  expect(api.mintUserNFT).not.toHaveBeenCalled()
})

const costOffer = {
  quote: { quoteId: 'receipt', chainType: 'Solana', network: 'Devnet', payerAddress: 'payer',
    limits: { networkFee: '5000', retainedRent: '9646560', protocolLevy: '10000000' }, expiresAt: '2999-01-01T00:00:00Z' },
  authorization: { quoteId: 'receipt', quoteHash: 'exact-hash', limits: { networkFee: '5000', retainedRent: '9646560', protocolLevy: '10000000' } },
}
it('requires explicit cost acceptance and retains that exact receipt across a lost-response reload', async () => {
  const saved = { key: 'original-key', body: { name: 'NFT', chainType: 'Solana', metadataURI: 'ipfs://metadata' }, costOffer }
  localStorage.setItem('azoa:nft-mint:user', JSON.stringify(saved))
  api.mintUserNFT.mockResolvedValue({ ok: false, error: { message: 'Response lost' } })
  const first = render(<UserNftsPage />)
  await screen.findByRole('button', { name: 'Accept creation costs and mint' })
  expect(api.mintUserNFT).not.toHaveBeenCalled()
  expect(screen.getByText('9646560 native base units')).toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: 'Accept creation costs and mint' }))
  await waitFor(() => expect(api.mintUserNFT).toHaveBeenCalledTimes(1))
  const accepted = api.mintUserNFT.mock.calls[0][0]
  expect(accepted.protocolCostAuthorization).toEqual(costOffer.authorization)
  expect(JSON.parse(localStorage.getItem('azoa:nft-mint:user')!).body.protocolCostAuthorization).toEqual(costOffer.authorization)
  first.unmount()
  render(<UserNftsPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Retry same request' }))
  await waitFor(() => expect(api.mintUserNFT).toHaveBeenCalledTimes(2))
  expect(api.mintUserNFT.mock.calls[1]).toEqual([accepted, 'original-key'])
  expect(api.quoteUserNftMintCosts).not.toHaveBeenCalled()
})
it('shows a newly fetched offer before allowing a saved Solana draft to mint', async () => {
  localStorage.setItem('azoa:nft-mint:user', JSON.stringify({ key: 'draft-key', body: { name: 'NFT', chainType: 'Solana', metadataURI: 'ipfs://metadata' } }))
  api.quoteUserNftMintCosts.mockResolvedValue({ ok: true, value: costOffer })
  render(<UserNftsPage />)
  fireEvent.click(await screen.findByRole('button', { name: 'Review creation costs' }))
  await screen.findByRole('button', { name: 'Accept creation costs and mint' })
  expect(api.quoteUserNftMintCosts).toHaveBeenCalledWith({ name: 'NFT', chainType: 'Solana', metadataURI: 'ipfs://metadata' }, 'draft-key')
  expect(api.mintUserNFT).not.toHaveBeenCalled()
  expect(JSON.parse(localStorage.getItem('azoa:nft-mint:user')!).body.protocolCostAuthorization).toBeUndefined()
})
