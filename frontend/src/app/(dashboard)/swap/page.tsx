'use client'

import { useState } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { ChainSwitcher } from '@/components/shared/chain-switcher'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ResultDisplay } from '@/components/shared/result-display'
import { oasis, isOk } from '@/lib/oasis'

const SLIPPAGE_PRESETS = [
  { label: '0.1%', bps: 10 },
  { label: '0.5%', bps: 50 },
  { label: '1%', bps: 100 },
]

interface SwapFormState {
  tokenIn: string
  tokenOut: string
  amount: string
  slippageBps: number
  customSlippage: string
  sender: string
}

interface SwapQuote {
  expectedAmountOut: string
  priceImpact: number
  fee: string
  route?: unknown
  amountIn: string
  tokenIn: string
  tokenOut: string
  chain: string
  /** Returned by the backend (Jupiter v2) — required to execute the swap. */
  quoteId?: string
}

function SwapChainForm({ chain }: { chain: string }) {
  const [form, setForm] = useState<SwapFormState>({
    tokenIn: chain === 'algorand' ? '0' : 'So11111111111111111111111111111111111111112',
    tokenOut: chain === 'algorand' ? '31566704' : 'EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v',
    amount: '1000000',
    slippageBps: 50,
    customSlippage: '',
    sender: chain === 'algorand' ? 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ' : '9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM',
  })
  const [quote, setQuote] = useState<SwapQuote | null>(null)
  const [rawQuote, setRawQuote] = useState<unknown>(null)
  const [unsignedTx, setUnsignedTx] = useState<unknown>(null)
  const [loadingQuote, setLoadingQuote] = useState(false)
  const [loadingBuild, setLoadingBuild] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = (field: keyof SwapFormState, value: string | number) =>
    setForm((prev) => ({ ...prev, [field]: value }))

  const handleGetQuote = async () => {
    setError(null)
    setQuote(null)
    setRawQuote(null)
    setUnsignedTx(null)
    setLoadingQuote(true)
    try {
      const result = await oasis.api.getSwapQuote({
        chain,
        tokenIn: form.tokenIn,
        tokenOut: form.tokenOut,
        amountIn: form.amount,
        slippageBps: form.slippageBps,
        walletAddress: form.sender || undefined,
      })
      if (isOk(result)) {
        setQuote(result.value as SwapQuote)
        setRawQuote(result.value)
      } else {
        setError(result.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoadingQuote(false)
    }
  }

  const handleBuildSwap = async () => {
    if (!quote) return
    if (!quote.quoteId) {
      setError('Quote is missing quoteId — cannot execute swap. (Backend did not return a quoteId for this chain.)')
      return
    }
    if (!form.sender) {
      setError('Sender wallet address is required to build the swap transaction.')
      return
    }
    setError(null)
    setLoadingBuild(true)
    try {
      const result = await oasis.api.executeSwap({
        chain,
        quoteId: quote.quoteId,
        walletAddress: form.sender,
      })
      if (isOk(result)) {
        const resp = result.value
        setUnsignedTx({
          chain,
          format: 'native' as const,
          swapTransaction: resp.swapTransaction,
          quoteId: resp.quoteId,
          lastValidBlockHeight: resp.lastValidBlockHeight,
          message: resp.message,
          description: `Unsigned swap: ${form.amount} ${form.tokenIn} → expected ${resp.expectedAmountOut ?? quote.expectedAmountOut}`,
        })
      } else {
        setError(result.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoadingBuild(false)
    }
  }

  const effectiveSlippage = form.customSlippage
    ? Number(form.customSlippage)
    : form.slippageBps

  return (
    <div className="space-y-6">
      {/* Swap Form */}
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Swap Parameters</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor={`${chain}-tokenIn`}>
                {chain === 'algorand' ? 'Token In (Asset ID)' : 'Token In (Mint Address)'}
              </Label>
              <Input
                id={`${chain}-tokenIn`}
                placeholder={chain === 'algorand' ? '0 (ALGO) or asset ID' : 'Mint address'}
                value={form.tokenIn}
                onChange={(e) => set('tokenIn', e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor={`${chain}-tokenOut`}>
                {chain === 'algorand' ? 'Token Out (Asset ID)' : 'Token Out (Mint Address)'}
              </Label>
              <Input
                id={`${chain}-tokenOut`}
                placeholder={chain === 'algorand' ? 'Asset ID' : 'Mint address'}
                value={form.tokenOut}
                onChange={(e) => set('tokenOut', e.target.value)}
              />
            </div>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <Label htmlFor={`${chain}-amount`}>Amount</Label>
              <Input
                id={`${chain}-amount`}
                type="number"
                placeholder="0.00"
                value={form.amount}
                onChange={(e) => set('amount', e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor={`${chain}-sender`}>Sender Address</Label>
              <Input
                id={`${chain}-sender`}
                placeholder="Your wallet address"
                value={form.sender}
                onChange={(e) => set('sender', e.target.value)}
              />
            </div>
          </div>

          {/* Slippage */}
          <div className="space-y-1.5">
            <Label>Slippage Tolerance</Label>
            <div className="flex flex-wrap gap-2 items-center">
              {SLIPPAGE_PRESETS.map((preset) => (
                <Button
                  key={preset.bps}
                  type="button"
                  size="sm"
                  variant={
                    form.slippageBps === preset.bps && !form.customSlippage
                      ? 'default'
                      : 'outline'
                  }
                  onClick={() => {
                    set('slippageBps', preset.bps)
                    set('customSlippage', '')
                  }}
                >
                  {preset.label}
                </Button>
              ))}
              <div className="flex items-center gap-1.5">
                <Input
                  className="w-24"
                  placeholder="Custom bps"
                  value={form.customSlippage}
                  onChange={(e) => {
                    set('customSlippage', e.target.value)
                    if (e.target.value) set('slippageBps', Number(e.target.value))
                  }}
                />
                <span className="text-xs text-muted-foreground">bps</span>
              </div>
            </div>
            <p className="text-xs text-muted-foreground">
              Active: {effectiveSlippage} bps ({(effectiveSlippage / 100).toFixed(2)}%)
            </p>
          </div>

          <Button onClick={handleGetQuote} disabled={loadingQuote || !form.tokenIn || !form.tokenOut || !form.amount}>
            {loadingQuote ? 'Fetching Quote...' : 'Get Quote'}
          </Button>
        </CardContent>
      </Card>

      {/* Error */}
      {error ? <ResultDisplay isError message={error} /> : null}

      {/* Quote Card */}
      {quote !== null ? (
        <Card className="border-blue-500/40 bg-blue-50/50 dark:bg-blue-950/20">
          <CardHeader className="pb-2">
            <CardTitle className="text-base text-blue-700 dark:text-blue-400">Swap Quote</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="grid gap-2 text-sm">
              <div className="flex justify-between">
                <span className="text-muted-foreground">Expected Output</span>
                <span className="font-mono font-medium">{quote.expectedAmountOut ?? '—'}</span>
              </div>
              <Separator />
              <div className="flex justify-between">
                <span className="text-muted-foreground">Price Impact</span>
                <span className={`font-mono ${(quote.priceImpact ?? 0) > 2 ? 'text-destructive' : 'text-green-600 dark:text-green-400'}`}>
                  {quote.priceImpact != null ? `${quote.priceImpact}%` : '—'}
                </span>
              </div>
              <Separator />
              <div className="flex justify-between">
                <span className="text-muted-foreground">Fee</span>
                <span className="font-mono">{quote.fee ?? '—'}</span>
              </div>
              {Array.isArray(quote.route) && quote.route.length > 0 && (
                <>
                  <Separator />
                  <div>
                    <p className="text-muted-foreground mb-1">Route Steps</p>
                    <div className="flex flex-wrap gap-1">
                      {(quote.route as unknown[]).map((step, i) => (
                        <Badge key={i} variant="secondary" className="font-mono text-xs">
                          {typeof step === 'string' ? step : JSON.stringify(step)}
                        </Badge>
                      ))}
                    </div>
                  </div>
                </>
              )}
            </div>

            <div className="pt-2">
              <p className="text-xs text-muted-foreground mb-2">Raw Quote</p>
              <div className="rounded-md bg-muted p-3 text-xs">
                <JsonViewer data={rawQuote} />
              </div>
            </div>

            <Button onClick={handleBuildSwap} disabled={loadingBuild} className="w-full">
              {loadingBuild ? 'Building Transaction...' : 'Build Swap Transaction'}
            </Button>
          </CardContent>
        </Card>
      ) : null}

      {/* Unsigned TX */}
      {unsignedTx !== null ? (
        <Card className="border-amber-500/40 bg-amber-50/50 dark:bg-amber-950/20">
          <CardHeader className="pb-2">
            <CardTitle className="text-sm text-amber-700 dark:text-amber-400">
              Unsigned Transaction Descriptor
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            <p className="text-xs text-muted-foreground">
              Sign and submit requires a wallet adapter — tx descriptor shown for verification.
            </p>
            <div className="rounded-md bg-muted p-3 text-xs">
              <JsonViewer data={unsignedTx} />
            </div>
          </CardContent>
        </Card>
      ) : null}
    </div>
  )
}

export default function SwapPage() {
  const [selectedChain, setSelectedChain] = useState<'algorand' | 'solana'>('algorand')

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">Swap</h1>
        <p className="text-sm text-muted-foreground">
          Get quotes and build swap transactions across chains
        </p>
      </div>

      <ChainSwitcher
        options={[
          { value: 'algorand', label: 'Algorand' },
          { value: 'solana', label: 'Solana' },
        ]}
        value={selectedChain}
        onChange={(v) => setSelectedChain(v as 'algorand' | 'solana')}
      />

      <div className="mt-4">
        <SwapChainForm chain={selectedChain} />
      </div>
    </div>
  )
}
