'use client'

import { useState } from 'react'
import { toast } from 'sonner'
import { SolanaProvider } from '@oasis/sdk'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { oasis, isOk } from '@/lib/oasis'
import { useNetwork } from '@/lib/network-context'
import { PROJECT_TOKEN, getBuyTargetTokenId, getPayWithTokenId } from '@/lib/networks'
import type { WalletEntry } from '@/lib/oasis-context'
import { Coins, ShoppingCart, AlertTriangle, ExternalLink } from 'lucide-react'

/** Matches .NET SwapQuoteResponse (camelCase JSON). */
interface SwapQuoteResponse {
  chain: string
  tokenIn: string
  tokenOut: string
  amountIn: string
  expectedAmountOut: string
  priceImpact: number
  fee: string
  quoteId?: string
  swapTransaction?: string
  message?: string
}

interface WalletFundDialogProps {
  wallet: WalletEntry
  open: boolean
  onOpenChange: (open: boolean) => void
}

const EXTERNAL_FAUCETS: Record<string, string> = {
  solana: 'https://faucet.solana.com',
  algorand: 'https://bank.testnet.algorand.network',
  ethereum: 'https://www.alchemy.com/faucets/ethereum-sepolia',
}

export function WalletFundDialog({ wallet, open, onOpenChange }: WalletFundDialogProps) {
  const { isTestLike, meta } = useNetwork()
  const chain = wallet.chainType.toLowerCase()

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            {isTestLike ? <Coins className="h-5 w-5" /> : <ShoppingCart className="h-5 w-5" />}
            {isTestLike ? 'Top up wallet' : `Buy ${PROJECT_TOKEN.symbol}`}
          </DialogTitle>
          <DialogDescription>
            {isTestLike
              ? `Fund this ${wallet.chainType} wallet with free ${meta.label} test tokens.`
              : `Swap into ${PROJECT_TOKEN.symbol} on ${meta.label} using the built-in DEX.`}
          </DialogDescription>
        </DialogHeader>

        {isTestLike ? (
          <TopUpBody wallet={wallet} chain={chain} onDone={() => onOpenChange(false)} />
        ) : (
          <BuyBody wallet={wallet} chain={chain} onDone={() => onOpenChange(false)} />
        )}
      </DialogContent>
    </Dialog>
  )
}

// ─── Top up (devnet / testnet) ───

function TopUpBody({
  wallet, chain, onDone,
}: { wallet: WalletEntry; chain: string; onDone: () => void }) {
  const defaultAmount = chain === 'solana' ? '1' : '5'
  const unit = chain === 'solana' ? 'SOL' : chain === 'algorand' ? 'ALGO' : ''
  const [amount, setAmount] = useState(defaultAmount)
  const [loading, setLoading] = useState(false)

  const unsupported = chain !== 'solana' && chain !== 'algorand'

  const handleTopUp = async () => {
    const amt = Number(amount)
    if (!Number.isFinite(amt) || amt <= 0) {
      toast.error('Enter a valid amount')
      return
    }
    setLoading(true)
    try {
      if (chain === 'solana') {
        // Solana faucet is a client-side RPC airdrop against the active network.
        const provider = oasis.wallet.getProvider<SolanaProvider>('solana')
        if (!provider) {
          toast.error('Solana provider not available')
          return
        }
        const res = await provider.requestAirdrop(wallet.address, amt)
        if (isOk(res)) {
          toast.success(`Requested ${amt} SOL airdrop — it may take a few seconds to confirm`)
          onDone()
        } else {
          toast.error(res.error.message)
        }
      } else {
        // Algorand faucet is server-side (pre-funded platform account).
        const res = await oasis.api.request<{ txHash?: string; message?: string }>(
          'POST', `/api/wallet/${wallet.id}/topup`, { amount: amt }
        )
        if (isOk(res)) {
          toast.success(res.value?.message || `Sent ${amt} ALGO to your wallet`)
          onDone()
        } else {
          toast.error(res.error.message)
        }
      }
    } finally {
      setLoading(false)
    }
  }

  if (unsupported) {
    const faucet = EXTERNAL_FAUCETS[chain]
    return (
      <div className="space-y-4">
        <div className="rounded-lg border border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-3 flex gap-2 text-xs text-amber-800 dark:text-amber-200">
          <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
          <p>In-app top-up isn&apos;t available for {wallet.chainType}. Use an external faucet for test funds.</p>
        </div>
        {faucet && (
          <Button
            variant="outline"
            className="w-full"
            render={<a href={faucet} target="_blank" rel="noopener noreferrer" />}
          >
            Open {wallet.chainType} faucet <ExternalLink className="ml-1.5 h-3.5 w-3.5" />
          </Button>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onDone}>Close</Button>
        </DialogFooter>
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <div className="space-y-1.5">
        <Label>Amount ({unit})</Label>
        <Input
          type="number"
          min="0"
          step="any"
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
        />
        <p className="text-xs text-muted-foreground">
          {chain === 'solana'
            ? 'Devnet/testnet airdrops are rate-limited by the public RPC.'
            : 'Sent from the platform faucet account (test networks only).'}
        </p>
      </div>
      <div className="rounded-lg bg-muted p-3 text-xs">
        <p className="text-muted-foreground">Destination</p>
        <p className="font-mono break-all">{wallet.address}</p>
      </div>
      <DialogFooter>
        <Button variant="outline" onClick={onDone} disabled={loading}>Cancel</Button>
        <Button onClick={handleTopUp} disabled={loading}>
          {loading ? 'Requesting…' : `Top up ${amount} ${unit}`}
        </Button>
      </DialogFooter>
    </div>
  )
}

// ─── Buy (mainnet) ───

function BuyBody({
  wallet, chain, onDone,
}: { wallet: WalletEntry; chain: string; onDone: () => void }) {
  const target = getBuyTargetTokenId(chain)
  const payWith = getPayWithTokenId(chain)
  const payUnit = chain === 'solana' ? 'SOL' : 'ALGO'
  const [amount, setAmount] = useState('0.1')
  const [quote, setQuote] = useState<SwapQuoteResponse | null>(null)
  const [loading, setLoading] = useState(false)

  const swapUnavailable = chain !== 'solana' && chain !== 'algorand'

  if (swapUnavailable || !target) {
    return (
      <div className="space-y-4">
        <div className="rounded-lg border border-amber-200 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-3 flex gap-2 text-xs text-amber-800 dark:text-amber-200">
          <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
          <p>
            {swapUnavailable
              ? `Buying ${PROJECT_TOKEN.symbol} isn't supported on ${wallet.chainType}.`
              : `The ${PROJECT_TOKEN.symbol} token isn't configured for ${wallet.chainType}. Set ${
                  chain === 'solana'
                    ? 'NEXT_PUBLIC_BUY_TOKEN_SOLANA_MINT'
                    : 'NEXT_PUBLIC_BUY_TOKEN_ALGORAND_ASSET_ID'
                }.`}
          </p>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onDone}>Close</Button>
        </DialogFooter>
      </div>
    )
  }

  const handleQuote = async () => {
    const amt = Number(amount)
    if (!Number.isFinite(amt) || amt <= 0) {
      toast.error('Enter a valid amount')
      return
    }
    setLoading(true)
    setQuote(null)
    const qs = new URLSearchParams({
      chain,
      tokenIn: payWith,
      tokenOut: target,
      amountIn: amount,
      slippageBps: '50',
      walletAddress: wallet.address,
    })
    const res = await oasis.api.request<SwapQuoteResponse>('GET', `/api/swap/quote?${qs.toString()}`)
    setLoading(false)
    if (isOk(res)) setQuote(res.value)
    else toast.error(res.error.message)
  }

  const handleConfirm = async () => {
    if (!quote?.quoteId) return
    setLoading(true)
    const res = await oasis.api.request<SwapQuoteResponse>('POST', '/api/swap/execute', {
      chain,
      quoteId: quote.quoteId,
      walletAddress: wallet.address,
    })
    setLoading(false)
    if (isOk(res)) {
      toast.success(
        res.value.message ||
          `Swap prepared. Sign the returned transaction with your ${wallet.chainType} wallet to complete the buy.`
      )
      onDone()
    } else {
      toast.error(res.error.message)
    }
  }

  return (
    <div className="space-y-4">
      <div className="space-y-1.5">
        <Label>Pay with ({payUnit})</Label>
        <Input
          type="number"
          min="0"
          step="any"
          value={amount}
          onChange={(e) => { setAmount(e.target.value); setQuote(null) }}
        />
      </div>

      {quote && (
        <div className="rounded-lg bg-muted p-3 space-y-1 text-xs">
          <div className="flex justify-between">
            <span className="text-muted-foreground">You receive (est.)</span>
            <span className="font-mono">{quote.expectedAmountOut} {PROJECT_TOKEN.symbol}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Price impact</span>
            <span className="font-mono">{(quote.priceImpact * 100).toFixed(2)}%</span>
          </div>
          <div className="flex justify-between">
            <span className="text-muted-foreground">Fee</span>
            <span className="font-mono">{quote.fee}</span>
          </div>
        </div>
      )}

      <p className="text-xs text-muted-foreground">
        The DEX returns an unsigned transaction; sign it with your connected wallet to finalise the buy.
      </p>

      <DialogFooter>
        <Button variant="outline" onClick={onDone} disabled={loading}>Cancel</Button>
        {!quote ? (
          <Button onClick={handleQuote} disabled={loading}>
            {loading ? 'Getting quote…' : 'Get quote'}
          </Button>
        ) : (
          <Button onClick={handleConfirm} disabled={loading || !quote.quoteId}>
            {loading ? 'Preparing…' : `Buy ${PROJECT_TOKEN.symbol}`}
          </Button>
        )}
      </DialogFooter>
    </div>
  )
}
