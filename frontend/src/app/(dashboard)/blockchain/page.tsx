'use client'

import { useState } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import { useChainInfo } from '@/lib/azoa-hooks'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { ChainSwitcher } from '@/components/shared/chain-switcher'
import { ResultDisplay } from '@/components/shared/result-display'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ChainBadge } from '@/components/shared/chain-badge'

// ─── Balance Checker ───

function BalanceChecker({ chain }: { chain: string }) {
  const [address, setAddress] = useState('')
  const [tokenId, setTokenId] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleCheck = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResult(null)
    const res = await azoa.wallet.getBalance(chain, address, tokenId || undefined)
    if (isOk(res)) {
      setResult(res.value)
      setIsError(false)
    } else {
      setResult(res.error.message)
      setIsError(true)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Balance Checker</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleCheck} className="space-y-3">
          <div className="space-y-1">
            <Label htmlFor={`${chain}-bal-addr`}>Address</Label>
            <Input
              id={`${chain}-bal-addr`}
              value={address}
              onChange={(e) => setAddress(e.target.value)}
              placeholder="Wallet address"
              required
            />
          </div>
          <div className="space-y-1">
            <Label htmlFor={`${chain}-bal-token`}>Token ID (optional)</Label>
            <Input
              id={`${chain}-bal-token`}
              value={tokenId}
              onChange={(e) => setTokenId(e.target.value)}
              placeholder="Token / Asset ID"
            />
          </div>
          <Button type="submit" disabled={loading} size="sm">
            {loading ? 'Checking…' : 'Check Balance'}
          </Button>
        </form>

        {result !== null && (
          <div className="mt-3">
            <ResultDisplay
              result={typeof result === 'string' ? undefined : result}
              message={typeof result === 'string' ? result : undefined}
              isError={isError}
            />
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── Address Validator ───

function AddressValidator({ chain }: { chain: string }) {
  const [address, setAddress] = useState('')
  const [loading, setLoading] = useState(false)
  const [valid, setValid] = useState<boolean | null>(null)
  const [error, setError] = useState<string | null>(null)

  const handleValidate = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setValid(null)
    setError(null)
    const res = await azoa.wallet.validateAddress(chain, address)
    if (isOk(res)) {
      setValid(res.value as boolean)
    } else {
      setError(res.error.message)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Address Validator</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleValidate} className="space-y-3">
          <div className="space-y-1">
            <Label htmlFor={`${chain}-val-addr`}>Address</Label>
            <Input
              id={`${chain}-val-addr`}
              value={address}
              onChange={(e) => setAddress(e.target.value)}
              placeholder="Address to validate"
              required
            />
          </div>
          <div className="flex items-center gap-3">
            <Button type="submit" disabled={loading} size="sm">
              {loading ? 'Validating…' : 'Validate'}
            </Button>
            {valid !== null && (
              <Badge variant={valid ? 'default' : 'destructive'}>
                {valid ? 'Valid' : 'Invalid'}
              </Badge>
            )}
            {error && (
              <span className="text-xs text-destructive">{error}</span>
            )}
          </div>
        </form>
      </CardContent>
    </Card>
  )
}

// ─── Transaction Lookup ───

function TransactionLookup({ chain }: { chain: string }) {
  const [txHash, setTxHash] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleLookup = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResult(null)
    const res = await azoa.wallet.getTransactionStatus(chain, txHash)
    if (isOk(res)) {
      setResult(res.value)
      setIsError(false)
    } else {
      setResult(res.error.message)
      setIsError(true)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Transaction Lookup</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleLookup} className="space-y-3">
          <div className="space-y-1">
            <Label htmlFor={`${chain}-tx`}>Transaction Hash</Label>
            <Input
              id={`${chain}-tx`}
              value={txHash}
              onChange={(e) => setTxHash(e.target.value)}
              placeholder="Transaction hash / signature"
              required
            />
          </div>
          <Button type="submit" disabled={loading} size="sm">
            {loading ? 'Looking up…' : 'Lookup'}
          </Button>
        </form>

        {result !== null && (
          <div className="mt-3">
            {typeof result === 'string' ? (
              <ResultDisplay message={result} isError={isError} />
            ) : (
              <div className="rounded-md border p-3">
                <JsonViewer data={result} />
              </div>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── Token Metadata ───

function TokenMetadata({ chain }: { chain: string }) {
  const [tokenId, setTokenId] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleFetch = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResult(null)
    const res = await azoa.api.request('GET', `/api/blockchain/${chain}/token/${tokenId}/metadata`)
    if (isOk(res)) {
      setResult(res.value)
      setIsError(false)
    } else {
      setResult(res.error.message)
      setIsError(true)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Token Metadata</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleFetch} className="space-y-3">
          <div className="space-y-1">
            <Label htmlFor={`${chain}-meta-token`}>Token ID</Label>
            <Input
              id={`${chain}-meta-token`}
              value={tokenId}
              onChange={(e) => setTokenId(e.target.value)}
              placeholder="Token / Asset ID"
              required
            />
          </div>
          <Button type="submit" disabled={loading} size="sm">
            {loading ? 'Fetching…' : 'Fetch Metadata'}
          </Button>
        </form>

        {result !== null && (
          <div className="mt-3">
            {typeof result === 'string' ? (
              <ResultDisplay message={result} isError={isError} />
            ) : (
              <div className="rounded-md border p-3">
                <JsonViewer data={result} />
              </div>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── Chain Info Card ───

function ChainInfoPanel({ chain }: { chain: string }) {
  const { info, loading, error } = useChainInfo(chain)

  return (
    <Card>
      <CardHeader className="pb-2">
        <CardTitle className="flex items-center gap-2 text-sm">
          <ChainBadge chain={chain} />
          <span className="capitalize">Chain Info</span>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {loading && (
          <div className="space-y-2">
            {Array.from({ length: 4 }).map((_, i) => (
              <div key={i} className="h-4 w-full animate-pulse rounded bg-muted" />
            ))}
          </div>
        )}
        {error && <p className="text-sm text-destructive">{error}</p>}
        {info && (
          <div className="space-y-2 text-sm">
            {Object.entries(info).map(([key, value]) => (
              <div key={key} className="flex justify-between gap-4">
                <span className="text-muted-foreground capitalize">
                  {key.replace(/([A-Z])/g, ' $1').trim()}
                </span>
                <span className="font-mono text-xs text-right break-all max-w-[60%]">
                  {String(value)}
                </span>
              </div>
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── Chain Panels ───

function ChainPanels({ chain }: { chain: string }) {
  return (
    <div className="space-y-4">
      <ChainInfoPanel chain={chain} />

      <Separator />

      <div className="grid gap-4 md:grid-cols-2">
        <BalanceChecker chain={chain} />
        <AddressValidator chain={chain} />
        <TransactionLookup chain={chain} />
        <TokenMetadata chain={chain} />
      </div>
    </div>
  )
}

// ─── Page ───

export default function BlockchainPage() {
  const [selectedChain, setSelectedChain] = useState<'algorand' | 'solana'>('algorand')

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">Blockchain</h1>
        <p className="text-sm text-muted-foreground">
          Explore balances, transactions, and token data across chains
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
        <ChainPanels chain={selectedChain} />
      </div>
    </div>
  )
}
