'use client'

import { useState, useEffect, useCallback } from 'react'
import { oasis, isOk } from '@/lib/oasis'
import { useOasisAuth } from '@/lib/oasis-auth'
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

// ─── Types ───

interface AvatarNft {
  id: string
  name: string
  chainType: string
  contractAddress?: string
  tokenId?: string
  [key: string]: unknown
}

interface BoundHolon {
  holonId: string
  permission?: string
}

interface BoundWallet {
  walletId: string
}

// ─── Mint AvatarNFT Form ───

function MintAvatarNftForm({ onMinted }: { onMinted: () => void }) {
  const [name, setName] = useState('')
  const [chainType, setChainType] = useState('')
  const [contractAddress, setContractAddress] = useState('')
  const [tokenId, setTokenId] = useState('')
  const [description, setDescription] = useState('')
  const [imageUri, setImageUri] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setResult(null)

    const body = {
      name,
      chainType,
      contractAddress: contractAddress || undefined,
      tokenId: tokenId || undefined,
      description: description || undefined,
      imageUri: imageUri || undefined,
    }

    const res = await oasis.api.request('POST', '/api/avatarnft/mint', body)
    if (isOk(res)) {
      setResult(res.value)
      setIsError(false)
      onMinted()
    } else {
      setResult((res.error as { message: string }).message)
      setIsError(true)
    }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Mint AvatarNFT</CardTitle>
      </CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1">
              <Label htmlFor="anft-name">Name</Label>
              <Input
                id="anft-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="My AvatarNFT"
                required
              />
            </div>
            <div className="space-y-1">
              <Label htmlFor="anft-chain">Chain Type</Label>
              <Select value={chainType} onValueChange={(v) => setChainType(v ?? '')} required>
                <SelectTrigger id="anft-chain">
                  <SelectValue placeholder="Select chain" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="algorand">Algorand</SelectItem>
                  <SelectItem value="solana">Solana</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>

          <div className="space-y-1">
            <Label htmlFor="anft-contract">Contract Address</Label>
            <Input
              id="anft-contract"
              value={contractAddress}
              onChange={(e) => setContractAddress(e.target.value)}
              placeholder="Contract address"
            />
          </div>

          <div className="space-y-1">
            <Label htmlFor="anft-token">Token ID</Label>
            <Input
              id="anft-token"
              value={tokenId}
              onChange={(e) => setTokenId(e.target.value)}
              placeholder="Token ID"
            />
          </div>

          <div className="space-y-1">
            <Label htmlFor="anft-desc">Description</Label>
            <Input
              id="anft-desc"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Describe this NFT"
            />
          </div>

          <div className="space-y-1">
            <Label htmlFor="anft-image">Image URI</Label>
            <Input
              id="anft-image"
              value={imageUri}
              onChange={(e) => setImageUri(e.target.value)}
              placeholder="https://..."
            />
          </div>

          <Button type="submit" disabled={loading} className="w-full">
            {loading ? 'Minting…' : 'Mint AvatarNFT'}
          </Button>
        </form>

        {result !== null && (
          <div className="mt-4">
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

// ─── Bind Holon Dialog ───

function BindHolonDialog({ avatarNftId, onBound }: { avatarNftId: string; onBound: () => void }) {
  const [open, setOpen] = useState(false)
  const [holonId, setHolonId] = useState('')
  const [permission, setPermission] = useState('read')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleBind = async () => {
    setLoading(true)
    setResult(null)
    const res = await oasis.api.request(
      'POST',
      `/api/avatarnft/${avatarNftId}/holons/${holonId}/bind`,
      { permission }
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
          <DialogTitle>Bind Holon to AvatarNFT</DialogTitle>
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

function BindWalletDialog({ avatarNftId, onBound }: { avatarNftId: string; onBound: () => void }) {
  const [open, setOpen] = useState(false)
  const [walletId, setWalletId] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleBind = async () => {
    setLoading(true)
    setResult(null)
    const res = await oasis.api.request(
      'POST',
      `/api/avatarnft/${avatarNftId}/wallets/${walletId}/bind`
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
          <DialogTitle>Bind Wallet to AvatarNFT</DialogTitle>
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
  const [avatarId, setAvatarId] = useState('')
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
    const res = await oasis.api.request('POST', '/api/avatarnft/verify-ownership', {
      avatarId,
      chainType,
      contractAddress,
      tokenId,
    })
    if (isOk(res)) {
      const data = res.value as { verified?: boolean; [k: string]: unknown }
      setResult(data)
      setIsError(false)
      setPassed(data?.verified ?? true)
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
              <Label>Avatar ID</Label>
              <Input
                value={avatarId}
                onChange={(e) => setAvatarId(e.target.value)}
                placeholder="Avatar ID"
                required
              />
            </div>
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
  const [avatarNftId, setAvatarNftId] = useState('')
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
    const res = await oasis.api.request('POST', '/api/avatarnft/verify-holon-access', {
      avatarNftId,
      holonId,
      permission,
    })
    if (isOk(res)) {
      const data = res.value as { hasAccess?: boolean; [k: string]: unknown }
      setResult(data)
      setIsError(false)
      setPassed(data?.hasAccess ?? true)
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
              <Label>AvatarNFT ID</Label>
              <Input
                value={avatarNftId}
                onChange={(e) => setAvatarNftId(e.target.value)}
                placeholder="AvatarNFT ID"
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

// ─── AvatarNFT Detail ───

function AvatarNftDetail({ nft }: { nft: AvatarNft }) {
  const [holons, setHolons] = useState<BoundHolon[]>([])
  const [wallets, setWallets] = useState<BoundWallet[]>([])
  const [composite, setComposite] = useState<unknown>(null)
  const [compositeLoading, setCompositeLoading] = useState(false)
  const [compositeError, setCompositeError] = useState(false)

  const fetchBindings = useCallback(async () => {
    const [holonRes, walletRes] = await Promise.all([
      oasis.api.request('GET', `/api/avatarnft/${nft.id}/holons`),
      oasis.api.request('GET', `/api/avatarnft/${nft.id}/wallets`),
    ])
    if (isOk(holonRes)) setHolons(holonRes.value)
    if (isOk(walletRes)) setWallets(walletRes.value)
  }, [nft.id])

  useEffect(() => { fetchBindings() }, [fetchBindings])

  const handleUnbindHolon = async (holonId: string) => {
    await oasis.api.request('DELETE', `/api/avatarnft/${nft.id}/holons/${holonId}/unbind`)
    fetchBindings()
  }

  const handleUnbindWallet = async (walletId: string) => {
    await oasis.api.request('DELETE', `/api/avatarnft/${nft.id}/wallets/${walletId}/unbind`)
    fetchBindings()
  }

  const fetchComposite = async () => {
    setCompositeLoading(true)
    setComposite(null)
    const res = await oasis.api.request('GET', `/api/avatarnft/${nft.id}/composite`)
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
          {nft.contractAddress && (
            <div className="flex gap-2">
              <span className="text-muted-foreground w-32">Contract</span>
              <span className="font-mono text-xs break-all">{nft.contractAddress}</span>
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

        {/* Binding actions */}
        <div className="flex flex-wrap gap-2">
          <BindHolonDialog avatarNftId={nft.id} onBound={fetchBindings} />
          <BindWalletDialog avatarNftId={nft.id} onBound={fetchBindings} />
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
                    {h.permission && <Badge variant="secondary">{h.permission}</Badge>}
                    <Button
                      variant="ghost"
                      size="sm"
                      className="h-6 text-destructive hover:text-destructive"
                      onClick={() => handleUnbindHolon(h.holonId)}
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
                    onClick={() => handleUnbindWallet(w.walletId)}
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

// ─── AvatarNFT List ───

function AvatarNftList({ avatarId }: { avatarId: string }) {
  const [nfts, setNfts] = useState<AvatarNft[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<AvatarNft | null>(null)

  const fetchNfts = useCallback(async () => {
    setLoading(true)
    setError(null)
    const res = await oasis.api.request('GET', `/api/avatarnft/avatar/${avatarId}`)
    if (isOk(res)) {
      setNfts(res.value)
    } else {
      setError((res.error as { message: string }).message)
    }
    setLoading(false)
  }, [avatarId])

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
          No AvatarNFTs found. Mint your first one above.
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

      {selected && <AvatarNftDetail nft={selected} />}
    </div>
  )
}

// ─── Page ───

export default function AvatarNftsPage() {
  const { avatarId } = useOasisAuth()
  const [refreshKey, setRefreshKey] = useState(0)

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">AvatarNFTs</h1>
        <p className="text-sm text-muted-foreground">
          Mint, bind, and verify avatar-bound NFTs
        </p>
      </div>

      <MintAvatarNftForm onMinted={() => setRefreshKey((k) => k + 1)} />

      <div>
        <h2 className="mb-3 text-sm font-medium text-muted-foreground uppercase tracking-wider">
          Your AvatarNFTs
        </h2>
        {avatarId ? (
          <AvatarNftList key={refreshKey} avatarId={avatarId} />
        ) : (
          <Card>
            <CardContent className="pt-6 text-center text-sm text-muted-foreground">
              Log in to view your AvatarNFTs.
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
