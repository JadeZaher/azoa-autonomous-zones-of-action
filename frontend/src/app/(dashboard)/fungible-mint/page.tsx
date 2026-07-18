'use client'

/**
 * Fungible Mint — live test harness (fungible-mint-and-render-model §11.3–11.5).
 *
 * The frontend IS the test harness for this track (there is no vitest/playwright
 * suite for the app). This page drives the new backend surface end-to-end against
 * the running .NET API via the azoa-sdk singleton:
 *
 *   (a) generate a custodial wallet (UNGATED — wallet-generate needs no KYC, §11.4);
 *   (b) attempt a one-shot fungible-mint and surface the KYC_FORBIDDEN 403 when the
 *       avatar is not KYC-approved (the value seam IS gated, §11.4);
 *   (c) render the wallet's holdings using ONLY the render-model DTO returned by the
 *       SINGLE portfolio call (§11.5) — no per-asset round-trips, no client-side
 *       decimals math (raw + display amounts arrive precomputed).
 */

import { useState, useCallback } from 'react'
import { toast } from 'sonner'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ErrorBanner } from '@/components/shared/error-banner'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoa } from '@/lib/azoa-context'
import type {
  PortfolioResult,
  PortfolioAsset,
  FungibleTokenResult,
} from 'azoa-sdk'
import type { WalletResult } from 'azoa-sdk/api'
import { Coins, Wallet, ShieldAlert, RefreshCw } from 'lucide-react'

function truncate(addr: string, chars = 6): string {
  return addr.length <= chars * 2 + 3 ? addr : `${addr.slice(0, chars)}…${addr.slice(-chars)}`
}

/** Detects the fail-closed KYC rejection the value seam surfaces (KYC_FORBIDDEN: → 403). */
function isKycForbidden(message: string): boolean {
  return message.includes('KYC_FORBIDDEN')
}

/**
 * Only render an asset icon when the ref is an https: URL. Guards against
 * javascript:/data:/http: schemes from chain-supplied metadata being injected
 * into an <img src>. Anything else falls back to the placeholder.
 */
function isHttpsUrl(ref: string | null | undefined): boolean {
  if (!ref) return false
  try {
    return new URL(ref).protocol === 'https:'
  } catch {
    return false
  }
}

export default function FungibleMintPage() {
  const { avatarId } = useAzoa()

  // ── (a) wallet generation ──
  const [chainType, setChainType] = useState('Algorand')
  const [wallet, setWallet] = useState<WalletResult | null>(null)
  const [walletBusy, setWalletBusy] = useState(false)
  const [walletError, setWalletError] = useState<string | null>(null)

  const generateWallet = useCallback(async () => {
    setWalletBusy(true)
    setWalletError(null)
    // Wallet-generate is intentionally UNGATED (§11.4) — no KYC required to make one.
    const result = await azoa.api.request<WalletResult>('POST', '/api/wallet/generate', {
      chainType,
      isDefault: false,
    })
    if (isOk(result)) {
      setWallet(result.value)
      toast.success('Wallet generated (ungated)')
    } else {
      setWalletError(result.error.message)
    }
    setWalletBusy(false)
  }, [chainType])

  // ── (b) fungible-mint ──
  const [name, setName] = useState('Project Share')
  const [unitName, setUnitName] = useState('PSHARE')
  const [total, setTotal] = useState('1000000')
  const [decimals, setDecimals] = useState('0')
  const [mintBusy, setMintBusy] = useState(false)
  const [mintResult, setMintResult] = useState<FungibleTokenResult | null>(null)
  const [mintError, setMintError] = useState<string | null>(null)
  const [kycBlocked, setKycBlocked] = useState(false)

  const fungibleMint = useCallback(async () => {
    setMintBusy(true)
    setMintError(null)
    setMintResult(null)
    setKycBlocked(false)

    const result = await azoa.api.fungibleMint(
      {
        chainType,
        name,
        unitName,
        total: Number(total),
        decimals: Number(decimals),
      },
      // A stable idempotency key so a re-click replays the original launch (no
      // double-mint) — exercises the idempotency path of the endpoint.
      { idempotencyKey: `harness:${avatarId}:${name}:${unitName}:${total}:${decimals}` }
    )

    if (isOk(result)) {
      setMintResult(result.value)
      toast.success(
        result.value.replayed
          ? `Replayed prior launch (ASA ${result.value.assetId})`
          : `Fungible token launched (ASA ${result.value.assetId})`
      )
    } else {
      const msg = result.error.message
      if (isKycForbidden(msg)) {
        // The value seam is KYC-gated (§11.4): a non-approved avatar is rejected
        // fail-closed with KYC_FORBIDDEN: → 403. This is the EXPECTED harness path
        // for an un-KYC'd avatar.
        setKycBlocked(true)
      } else {
        setMintError(msg)
      }
    }
    setMintBusy(false)
  }, [chainType, name, unitName, total, decimals, avatarId])

  // ── (c) render-model portfolio (ONE call) ──
  const [portfolio, setPortfolio] = useState<PortfolioResult | null>(null)
  const [portfolioBusy, setPortfolioBusy] = useState(false)
  const [portfolioError, setPortfolioError] = useState<string | null>(null)

  const loadPortfolio = useCallback(async () => {
    if (!wallet) return
    setPortfolioBusy(true)
    setPortfolioError(null)
    // ONE call returns the full render model — assets carry raw + display amounts
    // precomputed; the UI below reads ONLY result.value.assets.
    const result = await azoa.api.getWalletPortfolioRenderModel(wallet.id)
    if (isOk(result)) {
      setPortfolio(result.value)
    } else {
      setPortfolioError(result.error.message)
    }
    setPortfolioBusy(false)
  }, [wallet])

  if (!avatarId) {
    return (
      <Card>
        <CardContent className="pt-6">
          <p className="text-sm text-muted-foreground text-center">Sign in to use the fungible-mint harness.</p>
        </CardContent>
      </Card>
    )
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Fungible Mint — Live Harness</h1>
        <p className="text-sm text-muted-foreground">
          Drives the one-shot fungible-mint endpoint + render-model portfolio against the live backend.
          Wallet-generate is ungated; the mint seam is KYC-gated.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        {/* (a) Generate wallet */}
        <Card>
          <CardHeader className="pb-3">
            <CardTitle className="text-sm flex items-center gap-2">
              <Wallet className="h-4 w-4" /> 1. Generate Wallet (ungated)
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="space-y-1.5">
              <Label htmlFor="chain">Chain</Label>
              <Input id="chain" value={chainType} onChange={e => setChainType(e.target.value)} />
            </div>
            <Button size="sm" onClick={generateWallet} disabled={walletBusy}>
              {walletBusy ? 'Generating…' : 'Generate Wallet'}
            </Button>
            {walletError && <ErrorBanner message={walletError} onRetry={generateWallet} />}
            {wallet && (
              <div className="rounded-lg bg-muted p-3 text-sm space-y-1">
                <p><strong>Wallet:</strong> <span className="font-mono text-xs">{truncate(wallet.address, 8)}</span></p>
                <p><strong>Chain:</strong> {wallet.chainType}</p>
                <p className="text-xs text-muted-foreground font-mono">id {wallet.id}</p>
              </div>
            )}
          </CardContent>
        </Card>

        {/* (b) Fungible mint */}
        <Card>
          <CardHeader className="pb-3">
            <CardTitle className="text-sm flex items-center gap-2">
              <Coins className="h-4 w-4" /> 2. Fungible Mint (KYC-gated)
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <div className="space-y-1.5">
                <Label htmlFor="name">Name</Label>
                <Input id="name" value={name} onChange={e => setName(e.target.value)} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="unit">Unit Name</Label>
                <Input id="unit" value={unitName} onChange={e => setUnitName(e.target.value)} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="total">Total Supply</Label>
                <Input id="total" type="number" value={total} onChange={e => setTotal(e.target.value)} />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="decimals">Decimals</Label>
                <Input id="decimals" type="number" value={decimals} onChange={e => setDecimals(e.target.value)} />
              </div>
            </div>
            <Button size="sm" onClick={fungibleMint} disabled={mintBusy}>
              {mintBusy ? 'Minting…' : 'Attempt Fungible Mint'}
            </Button>

            {kycBlocked && (
              <div className="rounded-lg border border-amber-300 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-3 text-sm text-amber-800 dark:text-amber-300 flex items-start gap-2">
                <ShieldAlert className="h-4 w-4 mt-0.5 shrink-0" />
                <div>
                  <p className="font-medium">Blocked: KYC_FORBIDDEN (HTTP 403)</p>
                  <p className="text-xs mt-0.5">
                    The mint seam is fail-closed: this avatar is not KYC-approved, so the value-bearing
                    launch was rejected. Wallet generation above still succeeded — you can make a wallet,
                    but to mint you must be KYC-approved.
                  </p>
                </div>
              </div>
            )}
            {mintError && <ErrorBanner message={mintError} onRetry={fungibleMint} />}
            {mintResult && (
              <div className="rounded-lg bg-muted p-3 text-sm space-y-1">
                <p><strong>ASA id:</strong> <span className="font-mono text-xs">{mintResult.assetId}</span></p>
                <p><strong>Wallet:</strong> <span className="font-mono text-xs">{truncate(mintResult.walletAddress, 8)}</span></p>
                <p className="flex items-center gap-2">
                  <strong>Replayed:</strong>
                  <Badge variant={mintResult.replayed ? 'secondary' : 'default'} className="text-[10px]">
                    {mintResult.replayed ? 'yes (idempotent)' : 'no (fresh)'}
                  </Badge>
                </p>
              </div>
            )}
          </CardContent>
        </Card>
      </div>

      {/* (c) Render-model portfolio */}
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm flex items-center justify-between">
            <span className="flex items-center gap-2">
              <Coins className="h-4 w-4" /> 3. Portfolio (render-model — ONE call)
            </span>
            <Button size="sm" variant="outline" onClick={loadPortfolio} disabled={!wallet || portfolioBusy}>
              <RefreshCw className="h-3.5 w-3.5 mr-1.5" />
              {portfolioBusy ? 'Loading…' : 'Load Portfolio'}
            </Button>
          </CardTitle>
        </CardHeader>
        <CardContent>
          {!wallet ? (
            <p className="text-sm text-muted-foreground">Generate a wallet first.</p>
          ) : portfolioError ? (
            <ErrorBanner message={portfolioError} onRetry={loadPortfolio} />
          ) : !portfolio ? (
            <p className="text-sm text-muted-foreground">Click “Load Portfolio” to render from the render-model DTO.</p>
          ) : (
            <div className="space-y-3">
              <div className="flex justify-between text-xs text-muted-foreground">
                <span>Wallet {truncate(portfolio.address, 6)}</span>
                <span>Computed {new Date(portfolio.computedAt).toLocaleTimeString()}</span>
              </div>
              <Separator />
              {/* Render reads ONLY portfolio.assets (§11.5) — no second round-trip. */}
              {portfolio.assets.length === 0 ? (
                <p className="text-sm text-muted-foreground">No assets.</p>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Asset</TableHead>
                      <TableHead>Kind</TableHead>
                      <TableHead>Symbol</TableHead>
                      <TableHead className="text-right">Amount</TableHead>
                      <TableHead className="text-right">Raw</TableHead>
                      <TableHead>Chain</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {portfolio.assets.map((a: PortfolioAsset) => (
                      <TableRow key={`${a.kind}:${a.id}`}>
                        <TableCell className="flex items-center gap-2">
                          {isHttpsUrl(a.iconRef) ? (
                            // eslint-disable-next-line @next/next/no-img-element
                            <img
                              src={a.iconRef!}
                              alt=""
                              referrerPolicy="no-referrer"
                              className="h-5 w-5 rounded-full object-cover"
                            />
                          ) : (
                            <div className="h-5 w-5 rounded-full bg-muted" />
                          )}
                          <span className="text-sm">{a.name}</span>
                        </TableCell>
                        <TableCell>
                          <Badge variant="outline" className="text-[10px]">{a.kind}</Badge>
                        </TableCell>
                        <TableCell className="text-xs font-mono">{a.symbol}</TableCell>
                        <TableCell className="text-right font-mono text-sm">{a.displayAmount}</TableCell>
                        <TableCell className="text-right font-mono text-[11px] text-muted-foreground">
                          {a.rawAmount} <span className="opacity-60">(d{a.decimals})</span>
                        </TableCell>
                        <TableCell className="text-xs">{a.chain}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
