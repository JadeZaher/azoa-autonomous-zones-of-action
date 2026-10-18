import { useState, type ContextType } from 'react'
import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { NftLifecycleView } from 'azoa-sdk'
import { NftLifecyclePanel } from '@/components/shared/nft-lifecycle-panel'
import { AzoaContext } from '@/lib/azoa-context'
import { nftStageCostsApi } from '../msw/handlers/nft-stage-costs'
import { SLICE_TENANT_ID, renderSlice } from './render-slice'

const OTHER_USER = 'b2222222-2222-4222-8222-222222222222'
const parked = (payer: string): NftLifecycleView => ({
  operationId: 'c'.repeat(32), nftId: crypto.randomUUID(), operation: 'Transfer', state: 'Preparing', stage: 'Transfer',
  version: 4, preparationGeneration: 0, actorUserId: SLICE_TENANT_ID, recipientUserId: OTHER_USER, feeQuote: null,
  assetId: 'asset', transactionId: null, detail: null, needsStageCosts: true, stageCostPayerUserId: payer, acceptedStageCostLimits: null,
})

/** Holds the panel's value like a page does; only the React session context is hand-supplied, the SDK is real. */
function Lifecycle({ initial }: { initial: NftLifecycleView }) {
  const [value, setValue] = useState(initial)
  const session = { userId: SLICE_TENANT_ID, isAuthenticated: true, authLoading: false } as unknown as ContextType<typeof AzoaContext>
  return <AzoaContext.Provider value={session}><NftLifecyclePanel value={value} onChange={setValue} /></AzoaContext.Provider>
}

/** Recipient-stage Solana costs (F-M11): quote → review → accept by the payer, and an honest re-quote when costs move. */
describe('NFT stage costs slice', () => {
  it('the payer accepts exact costs, is told when they changed, and re-accepts before the stage prepares', async () => {
    const api = nftStageCostsApi(parked(SLICE_TENANT_ID))
    const { user } = await renderSlice(<Lifecycle initial={api.view} />, { apis: [api] })

    await user.click(await screen.findByRole('button', { name: 'Review stage costs' }))
    expect(await screen.findByText(/Stage costs on/)).toHaveTextContent('Stage costs on Solana Devnet')
    expect(screen.getByText(/Maximum network fee/)).toHaveTextContent('5000 native base units')
    await user.click(screen.getByRole('button', { name: 'Accept stage costs' }))
    expect(await screen.findByText(/Stage costs accepted/)).toBeInTheDocument()

    api.raiseCosts()
    await user.click(screen.getByRole('button', { name: 'Continue with accepted costs' }))
    expect(await screen.findByText(/network costs for this stage changed since you accepted them/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Review current stage costs' }))
    expect(await screen.findByText(/Maximum network fee/)).toHaveTextContent('9000 native base units')
    await user.click(screen.getByRole('button', { name: 'Accept stage costs' }))
    await user.click(await screen.findByRole('button', { name: 'Continue with accepted costs' }))

    expect(await screen.findByText('The transaction is ready for submission.')).toBeInTheDocument()
    expect(api.acceptedQuoteIds).toHaveLength(2)
    expect(new Set(api.acceptedQuoteIds).size).toBe(2)
  })

  it('a participant who is not the payer is told who must act and cannot quote', async () => {
    const api = nftStageCostsApi(parked(OTHER_USER))
    await renderSlice(<Lifecycle initial={api.view} />, { apis: [api] })

    expect(await screen.findByText('This stage continues after its paying participant accepts the network costs.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Review stage costs/ })).not.toBeInTheDocument()
  })
})
