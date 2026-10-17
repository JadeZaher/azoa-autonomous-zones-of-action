'use client'

import { useState, useEffect, useCallback, useRef } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { Separator } from '@/components/ui/separator'
import { ResultDisplay } from '@/components/shared/result-display'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ChainBadge } from '@/components/shared/chain-badge'
import { NftMoveActions } from '@/components/shared/nft-move-actions'
import { NftLifecyclePanel } from '@/components/shared/nft-lifecycle-panel'
import { useNftRequestScope } from '@/components/shared/use-nft-request-scope'
import { clearResolvedNftRequests } from '@/components/shared/nft-request-recovery'
import { NftCancelRequest } from '@/components/shared/nft-cancel-request'
import { NftProtocolCostReview } from '@/components/shared/nft-protocol-cost-review'
import { NftFeeReview } from '@/components/shared/nft-fee-review'
import { NftPendingOperations } from '@/components/shared/nft-pending-operations'
import type { NftLifecycleView, NftFeeQuote, NftProtocolCostOffer, UserNFTMintParams } from 'azoa-sdk'

// ─── Types ───

interface UserNft {
  id: string
  name: string
  chainType: string
  nftContractAddress?: string
  tokenId?: string
  [key: string]: unknown
}

interface BoundHolon {
  id: string
  holonId: string
  permissionLevel?: string
}

interface BoundWallet {
  id: string
  walletId: string
}

// ─── Mint UserNFT Form ───

function MintUserNftForm({ onStarted }: { onStarted: (value: NftLifecycleView) => void }) {
  const { userId } = useAzoaAuth()
  const [name, setName] = useState('')
  const [chainType, setChainType] = useState('Algorand')
  const generation = useNftRequestScope(`${userId}:${chainType}`)
  const [metadataURI, setMetadataURI] = useState('')
  const [walletId, setWalletId] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const request = useRef<{ key: string; operationId?: string; body: UserNFTMintParams; costOffer?: NftProtocolCostOffer } | null>(null)
  const [locked, setLocked] = useState(false)
  const [savedRequestKey, setSavedRequestKey] = useState<string | null>(null)
  const [corrupt, setCorrupt] = useState(false)
  const [costOffer, setCostOffer] = useState<NftProtocolCostOffer | null>(null)
  const [costAccepted, setCostAccepted] = useState(false)
  const [feeQuote, setFeeQuote] = useState<NftFeeQuote | null>(null)
  useEffect(() => {
    request.current = null; setSavedRequestKey(null); setLocked(false); setCorrupt(false)
    setCostOffer(null); setCostAccepted(false)
    if (!userId) return
    const stored = localStorage.getItem(`azoa:nft-mint:${userId}`)
    if (!stored) return
    try {
      const saved = JSON.parse(stored)
      if (typeof saved.key === 'string' && typeof saved.body?.name === 'string' && typeof saved.body?.metadataURI === 'string' && typeof saved.body?.chainType === 'string') {
        request.current = saved; setSavedRequestKey(saved.key); setName(saved.body.name); setChainType(saved.body.chainType)
        setMetadataURI(saved.body.metadataURI); setWalletId(saved.body.walletId ?? ''); setLocked(true)
        setCostOffer(saved.costOffer ?? null); setCostAccepted(Boolean(saved.body.protocolCostAuthorization))
      } else throw new Error('Invalid saved request')
    } catch { setCorrupt(true); setError('The saved mint request needs reconciliation before a new request.') }
  }, [userId])
  useEffect(() => { setFeeQuote(null); setLoading(false); setError(null) }, [chainType, userId])
  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!userId || corrupt) return
    const current = generation.current
    if (!request.current && (!feeQuote || feeQuote.chainType.toLowerCase() !== chainType.toLowerCase())) {
      setLoading(true); setError(null)
      try { const quote = await azoa.api.getNftFeeQuote(chainType, 'Mint'); if (generation.current !== current) return; if (isOk(quote)) setFeeQuote(quote.value); else setError(quote.error.message) }
      finally { if (generation.current === current) setLoading(false) }
      return
    }
    request.current ??= { key: crypto.randomUUID(), body: { name, chainType, metadataURI, walletId: walletId || undefined,
      feeAuthorization: feeQuote ? { revision: feeQuote.revision, maxNativeBaseUnits: feeQuote.nativeBaseUnits, quoteId: feeQuote.quoteId } : undefined } }
    if (userId) localStorage.setItem(`azoa:nft-mint:${userId}`, JSON.stringify(request.current))
    setSavedRequestKey(request.current.key); setLocked(true); setLoading(true); setError(null)
    try {
      const pending = request.current
      if (pending.body.chainType.toLowerCase() === 'solana' && !pending.body.protocolCostAuthorization) {
        if (!pending.costOffer || Date.parse(pending.costOffer.quote.expiresAt) <= Date.now()) {
          const quoted = await azoa.api.quoteUserNftMintCosts(pending.body, pending.key)
          if (generation.current !== current || request.current !== pending) return
          if (!isOk(quoted)) { setError(quoted.error.message); return }
          pending.costOffer = quoted.value
          localStorage.setItem(`azoa:nft-mint:${userId}`, JSON.stringify(pending))
          setCostOffer(quoted.value)
          return
        }
        pending.body.protocolCostAuthorization = pending.costOffer.authorization
        localStorage.setItem(`azoa:nft-mint:${userId}`, JSON.stringify(pending))
        setCostAccepted(true)
      }
      const result = await azoa.api.mintUserNFT(pending.body, pending.key)
      if (generation.current !== current || request.current !== pending) return
      if (isOk(result)) {
        pending.operationId = result.value.operationId
        localStorage.setItem(`azoa:nft-mint:${userId}`, JSON.stringify(pending))
        onStarted(result.value)
      }
      else setError(result.error.message)
    } finally { if (generation.current === current) setLoading(false) }
  }
  return <Card>
    <CardHeader><CardTitle className="text-sm">Mint UserNFT</CardTitle></CardHeader>
    <CardContent>
      <form onSubmit={submit} className="space-y-3">
        <div><Label htmlFor="user-nft-name">Name</Label><Input id="user-nft-name" required value={name} disabled={locked} onChange={e => setName(e.target.value)} /></div>
        <div><Label htmlFor="user-nft-chain">Chain</Label>
          <Select value={chainType} disabled={locked} onValueChange={value => setChainType(value ?? 'Algorand')}>
            <SelectTrigger id="user-nft-chain"><SelectValue /></SelectTrigger>
            <SelectContent><SelectItem value="Algorand">Algorand</SelectItem><SelectItem value="Ethereum">Ethereum</SelectItem><SelectItem value="Solana">Solana</SelectItem></SelectContent>
          </Select>
        </div>
        <div><Label htmlFor="user-nft-metadata">Metadata URI</Label><Input id="user-nft-metadata" required value={metadataURI} disabled={locked} onChange={e => setMetadataURI(e.target.value)} placeholder="ipfs://... or https://..." /></div>
        <div><Label htmlFor="user-nft-wallet">Wallet ID (optional)</Label><Input id="user-nft-wallet" value={walletId} disabled={locked} onChange={e => setWalletId(e.target.value)} /></div>
        {feeQuote?.chainType.toLowerCase() === chainType.toLowerCase() && <NftFeeReview quote={feeQuote} />}
        {costOffer && <NftProtocolCostReview offer={costOffer} accepted={costAccepted} />}
        <Button type="submit" disabled={loading || corrupt || !userId}>{loading ? 'Loading...' : locked ? chainType.toLowerCase() === 'solana' && !costAccepted ? costOffer ? 'Accept creation costs and mint' : 'Review creation costs' : 'Retry same request' : feeQuote?.chainType.toLowerCase() === chainType.toLowerCase() ? chainType.toLowerCase() === 'solana' ? 'Authorize fee and review creation costs' : 'Authorize fee and mint' : 'Review mint fee'}</Button>
        {locked && savedRequestKey && <NftCancelRequest operation="Mint" requestKey={savedRequestKey} storageKey={`azoa:nft-mint:${userId}`} onCancelled={value => { request.current = null; setSavedRequestKey(null); setLocked(false); setFeeQuote(null); setCostOffer(null); setCostAccepted(false); setError(null); onStarted(value) }} />}
        {locked && <p className="text-sm text-muted-foreground">Retries reuse this request. Confirm its outcome before creating another NFT.</p>}
        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
      </form>
    </CardContent>
  </Card>
}
function BindHolonDialog({ userNftId, onBound }: { userNftId: string; onBound: () => void }) {
  const [open, setOpen] = useState(false)
  const [holonId, setHolonId] = useState('')
  const [permission, setPermission] = useState('read')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleBind = async () => {
    setLoading(true)
    setResult(null)
    const res = await azoa.api.request(
      'POST',
      `/api/usernft/${userNftId}/holons/${holonId}/bind`,
      { role: "member", permissionLevel: permission }
    )
    if (isOk(res)) {
      setResult(res.value)
      setIsError(false)
      onBound()
    } else {
      setResult((res.error as { message: string }).message)
      setIsError(true)
    }
    setLoading(false)
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="outline" size="sm">Bind Holon</Button>} />
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Bind Holon to UserNFT</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1">
            <Label>Holon ID</Label>
            <Input
              value={holonId}
              onChange={(e) => setHolonId(e.target.value)}
              placeholder="Holon ID"
            />
          </div>
          <div className="space-y-1">
            <Label>Permission</Label>
            <Select value={permission} onValueChange={(v) => setPermission(v ?? 'read')}>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="read">Read</SelectItem>
                <SelectItem value="write">Write</SelectItem>
                <SelectItem value="admin">Admin</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <Button onClick={handleBind} disabled={loading || !holonId} className="w-full">
            {loading ? 'Binding…' : 'Bind Holon'}
          </Button>
          {result !== null && (
            <ResultDisplay
              result={typeof result === 'string' ? undefined : result}
              message={typeof result === 'string' ? result : undefined}
              isError={isError}
            />
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ─── Bind Wallet Dialog ───

function BindWalletDialog({ userNftId, onBound }: { userNftId: string; onBound: () => void }) {
  const [open, setOpen] = useState(false)
  const [walletId, setWalletId] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleBind = async () => {
    setLoading(true)
    setResult(null)
    const res = await azoa.api.request(
      'POST',
      `/api/usernft/${userNftId}/wallets/${walletId}/bind`,
      { bindingType: "access", accessLevel: "read" }
    )
    if (isOk(res)) {
      setResult(res.value)
      setIsError(false)
      onBound()
    } else {
      setResult((res.error as { message: string }).message)
      setIsError(true)
    }
    setLoading(false)
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="outline" size="sm">Bind Wallet</Button>} />
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Bind Wallet to UserNFT</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1">
            <Label>Wallet ID (GUID)</Label>
            <Input
              value={walletId}
              onChange={(e) => setWalletId(e.target.value)}
              placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
            />
          </div>
          <Button onClick={handleBind} disabled={loading || !walletId} className="w-full">
            {loading ? 'Binding…' : 'Bind Wallet'}
          </Button>
          {result !== null && (
            <ResultDisplay
              result={typeof result === 'string' ? undefined : result}
              message={typeof result === 'string' ? result : undefined}
              isError={isError}
            />
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ─── Verify Ownership Panel ───

function VerifyOwnershipPanel() {
  const [chainType, setChainType] = useState('')
  const [contractAddress, setContractAddress] = useState('')
  const [tokenId, setTokenId] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)
  const [passed, setPassed] = useState<boolean | null>(null)

  const handleVerify = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResult(null)
    setPassed(null)
    const res = await azoa.api.verifyNFTOwnership({
      chainType,
      nftContractAddress: contractAddress,
      tokenId,
    })
    if (isOk(res)) {
      const data = res.value
      setResult(data)
      setIsError(false)
      setPassed(data === true)
    } else {
      setResult((res.error as { message: string }).message)
      setIsError(true)
      setPassed(false)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Verify Ownership</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleVerify} className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label>Chain Type</Label>
              <Select value={chainType} onValueChange={(v) => setChainType(v ?? '')} required>
                <SelectTrigger>
                  <SelectValue placeholder="Select chain" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="algorand">Algorand</SelectItem>
                  <SelectItem value="solana">Solana</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1">
              <Label>Contract Address</Label>
              <Input
                value={contractAddress}
                onChange={(e) => setContractAddress(e.target.value)}
                placeholder="Contract address"
                required
              />
            </div>
            <div className="space-y-1">
              <Label>Token ID</Label>
              <Input
                value={tokenId}
                onChange={(e) => setTokenId(e.target.value)}
                placeholder="Token ID"
                required
              />
            </div>
          </div>
          <Button type="submit" disabled={loading} size="sm">
            {loading ? 'Verifying…' : 'Verify Ownership'}
          </Button>
          {passed !== null && (
            <Badge variant={passed ? 'default' : 'destructive'} className="ml-2">
              {passed ? 'Verified' : 'Not Verified'}
            </Badge>
          )}
          {result !== null && (
            <ResultDisplay
              result={typeof result === 'string' ? undefined : result}
              message={typeof result === 'string' ? result : undefined}
              isError={isError}
            />
          )}
        </form>
      </CardContent>
    </Card>
  )
}

// ─── Verify Holon Access Panel ───

function VerifyHolonAccessPanel() {
  const [userNftId, setUserNftId] = useState('')
  const [holonId, setHolonId] = useState('')
  const [permission, setPermission] = useState('read')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)
  const [passed, setPassed] = useState<boolean | null>(null)

  const handleVerify = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResult(null)
    setPassed(null)
    const res = await azoa.api.verifyHolonAccess({
      userNFTId: userNftId,
      holonId,
      requiredPermission: permission,
    })
    if (isOk(res)) {
      const data = res.value
      setResult(data)
      setIsError(false)
      setPassed(data === true)
    } else {
      setResult((res.error as { message: string }).message)
      setIsError(true)
      setPassed(false)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Verify Holon Access</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleVerify} className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label>UserNFT ID</Label>
              <Input
                value={userNftId}
                onChange={(e) => setUserNftId(e.target.value)}
                placeholder="UserNFT ID"
                required
              />
            </div>
            <div className="space-y-1">
              <Label>Holon ID</Label>
              <Input
                value={holonId}
                onChange={(e) => setHolonId(e.target.value)}
                placeholder="Holon ID"
                required
              />
            </div>
            <div className="space-y-1">
              <Label>Permission</Label>
              <Select value={permission} onValueChange={(v) => setPermission(v ?? 'read')}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="read">Read</SelectItem>
                  <SelectItem value="write">Write</SelectItem>
                  <SelectItem value="admin">Admin</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
          <Button type="submit" disabled={loading} size="sm">
            {loading ? 'Verifying…' : 'Verify Access'}
          </Button>
          {passed !== null && (
            <Badge variant={passed ? 'default' : 'destructive'} className="ml-2">
              {passed ? 'Access Granted' : 'Access Denied'}
            </Badge>
          )}
          {result !== null && (
            <ResultDisplay
              result={typeof result === 'string' ? undefined : result}
              message={typeof result === 'string' ? result : undefined}
              isError={isError}
            />
          )}
        </form>
      </CardContent>
    </Card>
  )
}

// ─── UserNFT Detail ───

function UserNftDetail({ nft, onStarted }: { nft: UserNft; onStarted: (value: NftLifecycleView) => void }) {
  const [holons, setHolons] = useState<BoundHolon[]>([])
  const [wallets, setWallets] = useState<BoundWallet[]>([])
  const [composite, setComposite] = useState<unknown>(null)
  const [compositeLoading, setCompositeLoading] = useState(false)
  const [compositeError, setCompositeError] = useState(false)

  const fetchBindings = useCallback(async () => {
    const [holonRes, walletRes] = await Promise.all([
      azoa.api.request<BoundHolon[]>('GET', `/api/usernft/${nft.id}/holons`),
      azoa.api.request<BoundWallet[]>('GET', `/api/usernft/${nft.id}/wallets`),
    ])
    if (isOk(holonRes)) setHolons(holonRes.value)
    if (isOk(walletRes)) setWallets(walletRes.value)
  }, [nft.id])

  useEffect(() => { fetchBindings() }, [fetchBindings])

  const handleUnbindHolon = async (holonId: string) => {
    await azoa.api.request('DELETE', `/api/usernft/holons/${holonId}`)
    fetchBindings()
  }

  const handleUnbindWallet = async (walletId: string) => {
    await azoa.api.request('DELETE', `/api/usernft/wallets/${walletId}`)
    fetchBindings()
  }

  const fetchComposite = async () => {
    setCompositeLoading(true)
    setComposite(null)
    const res = await azoa.api.request('GET', `/api/usernft/${nft.id}/composite`)
    if (isOk(res)) {
      setComposite(res.value)
      setCompositeError(false)
    } else {
      setComposite((res.error as { message: string }).message)
      setCompositeError(true)
    }
    setCompositeLoading(false)
  }

  return (
    <Card className="border-primary/30">
      <CardHeader className="pb-2">
        <CardTitle className="flex items-center gap-2 text-sm">
          <span>{nft.name}</span>
          <ChainBadge chain={nft.chainType} />
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="text-sm space-y-1">
          <div className="flex gap-2">
            <span className="text-muted-foreground w-32">ID</span>
            <span className="font-mono text-xs break-all">{nft.id}</span>
          </div>
          {nft.nftContractAddress && (
            <div className="flex gap-2">
              <span className="text-muted-foreground w-32">Contract</span>
              <span className="font-mono text-xs break-all">{nft.nftContractAddress}</span>
            </div>
          )}
          {nft.tokenId && (
            <div className="flex gap-2">
              <span className="text-muted-foreground w-32">Token ID</span>
              <span className="font-mono text-xs">{nft.tokenId}</span>
            </div>
          )}
        </div>

        <Separator />

        <NftMoveActions key={`${nft.id}`} nftId={nft.id} chainType={nft.chainType} transferable={nft.isTransferable === true && nft.isSoulbound !== true} onStarted={onStarted} />
        {/* Binding actions */}
        <div className="flex flex-wrap gap-2">
          <BindHolonDialog userNftId={nft.id} onBound={fetchBindings} />
          <BindWalletDialog userNftId={nft.id} onBound={fetchBindings} />
          <Button variant="outline" size="sm" onClick={fetchComposite} disabled={compositeLoading}>
            {compositeLoading ? 'Loading…' : 'View Composite'}
          </Button>
        </div>

        {/* Bound holons */}
        {holons.length > 0 && (
          <div>
            <p className="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
              Bound Holons
            </p>
            <div className="space-y-1">
              {holons.map((h) => (
                <div key={h.holonId} className="flex items-center justify-between rounded-md border px-3 py-1.5 text-sm">
                  <span className="font-mono text-xs truncate">{h.holonId}</span>
                  <div className="flex items-center gap-2">
                    {h.permissionLevel && <Badge variant="secondary">{h.permissionLevel}</Badge>}
                    <Button
                      variant="ghost"
                      size="sm"
                      className="h-6 text-destructive hover:text-destructive"
                      onClick={() => handleUnbindHolon(h.id)}
                    >
                      Unbind
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Bound wallets */}
        {wallets.length > 0 && (
          <div>
            <p className="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
              Bound Wallets
            </p>
            <div className="space-y-1">
              {wallets.map((w) => (
                <div key={w.walletId} className="flex items-center justify-between rounded-md border px-3 py-1.5 text-sm">
                  <span className="font-mono text-xs truncate">{w.walletId}</span>
                  <Button
                    variant="ghost"
                    size="sm"
                    className="h-6 text-destructive hover:text-destructive"
                    onClick={() => handleUnbindWallet(w.id)}
                  >
                    Unbind
                  </Button>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Composite */}
        {composite !== null && (
          <div>
            <Separator className="mb-3" />
            <p className="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider">
              Composite View
            </p>
            {compositeError ? (
              <ResultDisplay message={String(composite)} isError />
            ) : (
              <JsonViewer data={composite} />
            )}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── UserNFT List ───

function UserNftList({ userId, onStarted }: { userId: string; onStarted: (value: NftLifecycleView) => void }) {
  const [nfts, setNfts] = useState<UserNft[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<UserNft | null>(null)

  const fetchNfts = useCallback(async () => {
    setLoading(true)
    setError(null)
    const res = await azoa.api.request<UserNft[]>('GET', `/api/usernft/user/${userId}`)
    if (isOk(res)) {
      setNfts(res.value)
    } else {
      setError((res.error as { message: string }).message)
    }
    setLoading(false)
  }, [userId])

  useEffect(() => { fetchNfts() }, [fetchNfts])

  if (loading) {
    return (
      <Card className="animate-pulse">
        <CardContent className="h-24 pt-6" />
      </Card>
    )
  }

  if (error) {
    return <ResultDisplay message={error} isError />
  }

  if (nfts.length === 0) {
    return (
      <Card>
        <CardContent className="pt-6 text-center text-sm text-muted-foreground">
          No UserNFTs found. Mint your first one above.
        </CardContent>
      </Card>
    )
  }

  return (
    <div className="space-y-4">
      <Card>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Name</TableHead>
              <TableHead>Chain</TableHead>
              <TableHead>Token ID</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {nfts.map((nft) => (
              <TableRow
                key={nft.id}
                className={`cursor-pointer ${selected?.id === nft.id ? 'bg-muted/50' : ''}`}
                onClick={() => setSelected(selected?.id === nft.id ? null : nft)}
              >
                <TableCell className="font-medium">{nft.name}</TableCell>
                <TableCell>
                  <ChainBadge chain={nft.chainType} />
                </TableCell>
                <TableCell className="font-mono text-xs">{nft.tokenId ?? '—'}</TableCell>
                <TableCell className="text-right text-xs text-muted-foreground">
                  {selected?.id === nft.id ? 'selected' : 'click to select'}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      {selected && <UserNftDetail nft={selected} onStarted={onStarted} />}
    </div>
  )
}

// ─── Page ───

export default function UserNftsPage() {
  const { userId } = useAzoaAuth()
  const [refreshKey, setRefreshKey] = useState(0)
  const [lifecycleOwner, setLifecycleOwner] = useState(userId)
  const [lifecycle, setLifecycle] = useState<NftLifecycleView | null>(null)
  const [mintKey, setMintKey] = useState(0)
  const generation = useNftRequestScope(userId ?? "")
  const recordLifecycle = (value: NftLifecycleView) => {
    setLifecycleOwner(userId); setLifecycle(value)
    if (userId) localStorage.setItem(`azoa:nft-operation:${userId}`, JSON.stringify(value))
    if (userId) {
      const current = generation.current
      void clearResolvedNftRequests(userId, value).then(cleared => {
        if (cleared && generation.current === current) { setMintKey(key => key + 1); setRefreshKey(key => key + 1) }
      })
    }
    if (value.state === "Completed") setRefreshKey(key => key + 1)
  }
  useEffect(() => {
    setLifecycleOwner(userId); setLifecycle(null)
    if (!userId) return
    const stored = localStorage.getItem(`azoa:nft-operation:${userId}`)
    if (stored) { try { const value = JSON.parse(stored); if (typeof value.operationId === "string") setLifecycle(value) } catch { localStorage.removeItem(`azoa:nft-operation:${userId}`) } }
  }, [userId])

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">UserNFTs</h1>
        <p className="text-sm text-muted-foreground">
          Mint, bind, and verify user-bound NFTs
        </p>
      </div>

      <MintUserNftForm key={`${userId}:${mintKey}`} onStarted={recordLifecycle} />
      {userId && <NftPendingOperations key={userId} userId={userId} onSelect={recordLifecycle} />}
      <NftLifecyclePanel key={userId} value={lifecycleOwner === userId ? lifecycle : null} onChange={recordLifecycle} />
      {lifecycleOwner === userId && lifecycle && ["Completed", "Cancelled", "Failed"].includes(lifecycle.state) && <Button variant="outline" onClick={() => { setLifecycle(null); setMintKey(key => key + 1); if (userId) { localStorage.removeItem(`azoa:nft-operation:${userId}`); const saved = localStorage.getItem(`azoa:nft-mint:${userId}`); if (saved) { try { if (JSON.parse(saved).operationId === lifecycle.operationId) localStorage.removeItem(`azoa:nft-mint:${userId}`) } catch { /* Preserve unreconciled request. */ } } } }}>Start another NFT</Button>}

      <div>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground uppercase tracking-wider">
          Your UserNFTs
        </h2>
        {userId ? (
          <UserNftList key={`${userId}:${refreshKey}`} userId={userId} onStarted={recordLifecycle} />
        ) : (
          <Card>
            <CardContent className="pt-6 text-center text-sm text-muted-foreground">
              Log in to view your UserNFTs.
            </CardContent>
          </Card>
        )}
      </div>

      <Separator />

      <div>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground uppercase tracking-wider">
          Verification
        </h2>
        <div className="grid gap-4 md:grid-cols-2">
          <VerifyOwnershipPanel />
          <VerifyHolonAccessPanel />
        </div>
      </div>
    </div>
  )
}
