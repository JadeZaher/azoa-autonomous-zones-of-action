'use client'

import { useState, useEffect, useCallback } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import { toast } from 'sonner'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogTrigger,
} from '@/components/ui/dialog'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { ChainBadge } from '@/components/shared/chain-badge'
import { ResultDisplay } from '@/components/shared/result-display'
import { JsonViewer } from '@/components/shared/json-viewer'

interface NftRecord { id: string; name: string; chainId: string; tokenId?: string; isActive?: boolean; [k: string]: unknown }

// ── Mint ──

function MintNftForm() {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [chainId, setChainId] = useState('')
  const [walletId, setWalletId] = useState('')
  const [imageUri, setImageUri] = useState('')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [isError, setIsError] = useState(false)

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true); setResult(null)
    const res = await azoa.api.mintNft({ walletId, name, description, chainId, imageUri: imageUri || undefined })
    if (isOk(res)) { setResult(res.value); setIsError(false) }
    else { setResult(res.error.message); setIsError(true) }
    setLoading(false)
  }

  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Mint NFT</CardTitle></CardHeader>
      <CardContent>
        <form onSubmit={handleSubmit} className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5"><Label>Name</Label><Input value={name} onChange={e => setName(e.target.value)} placeholder="My NFT" required /></div>
            <div className="space-y-1.5"><Label>Chain</Label>
              <Select value={chainId} onValueChange={v => setChainId(v ?? '')} required>
                <SelectTrigger><SelectValue placeholder="Select" /></SelectTrigger>
                <SelectContent><SelectItem value="algorand">Algorand</SelectItem><SelectItem value="solana">Solana</SelectItem></SelectContent>
              </Select>
            </div>
          </div>
          <div className="space-y-1.5"><Label>Description</Label><Input value={description} onChange={e => setDescription(e.target.value)} /></div>
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1.5"><Label>Wallet ID</Label><Input value={walletId} onChange={e => setWalletId(e.target.value)} placeholder="GUID" required /></div>
            <div className="space-y-1.5"><Label>Image URI</Label><Input value={imageUri} onChange={e => setImageUri(e.target.value)} placeholder="https://…" /></div>
          </div>
          <Button type="submit" disabled={loading} size="sm" className="w-full">{loading ? 'Minting…' : 'Mint'}</Button>
        </form>
        {result !== null && <div className="mt-3"><ResultDisplay result={typeof result === 'string' ? undefined : result} message={typeof result === 'string' ? result : undefined} isError={isError} /></div>}
      </CardContent>
    </Card>
  )
}

// ── Actions ──

function TransferDialog({ nftId, onDone }: { nftId: string; onDone: () => void }) {
  const [open, setOpen] = useState(false)
  const [targetAvatarId, setTargetAvatarId] = useState('')
  const [walletId, setWalletId] = useState('')
  const [loading, setLoading] = useState(false)
  const handleTransfer = async () => {
    setLoading(true)
    const res = await azoa.api.transferNft(nftId, { targetAvatarId, walletId })
    if (isOk(res)) { onDone() } else { toast.error(res.error.message) }
    setLoading(false); setOpen(false)
  }
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="outline" size="sm">Transfer</Button>} />
      <DialogContent>
        <DialogHeader><DialogTitle>Transfer NFT</DialogTitle></DialogHeader>
        <div className="space-y-3">
          <div className="space-y-1.5"><Label>Target Avatar ID</Label><Input value={targetAvatarId} onChange={e => setTargetAvatarId(e.target.value)} placeholder="GUID" /></div>
          <div className="space-y-1.5"><Label>Wallet ID</Label><Input value={walletId} onChange={e => setWalletId(e.target.value)} placeholder="GUID" /></div>
          <Button onClick={handleTransfer} disabled={loading} size="sm" className="w-full">{loading ? 'Transferring…' : 'Transfer'}</Button>
        </div>
      </DialogContent>
    </Dialog>
  )
}

function BurnDialog({ nftId, onDone }: { nftId: string; onDone: () => void }) {
  const [open, setOpen] = useState(false)
  const [walletId, setWalletId] = useState('')
  const [loading, setLoading] = useState(false)
  const handleBurn = async () => {
    setLoading(true)
    const res = await azoa.api.burnNft(nftId, { walletId })
    if (isOk(res)) { onDone() } else { toast.error(res.error.message) }
    setLoading(false); setOpen(false)
  }
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="destructive" size="sm">Burn</Button>} />
      <DialogContent>
        <DialogHeader><DialogTitle>Burn NFT</DialogTitle></DialogHeader>
        <p className="text-sm text-muted-foreground">This is irreversible.</p>
        <div className="space-y-1.5"><Label>Wallet ID</Label><Input value={walletId} onChange={e => setWalletId(e.target.value)} placeholder="GUID" /></div>
        <Button variant="destructive" onClick={handleBurn} disabled={loading} size="sm" className="w-full">{loading ? 'Burning…' : 'Confirm'}</Button>
      </DialogContent>
    </Dialog>
  )
}

// ── Detail ──

function NftDetail({ nft, onRefresh }: { nft: NftRecord; onRefresh: () => void }) {
  const [metaResult, setMetaResult] = useState<unknown>(null)
  const [metaError, setMetaError] = useState(false)
  const [metaLoading, setMetaLoading] = useState(false)

  const fetchMeta = async () => {
    setMetaLoading(true); setMetaResult(null)
    const res = await azoa.api.getNftMetadata(nft.id)
    if (isOk(res)) { setMetaResult(res.value); setMetaError(false) }
    else { setMetaResult(res.error.message); setMetaError(true) }
    setMetaLoading(false)
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-sm">
          {nft.name} <ChainBadge chain={nft.chainId} />
          {nft.isActive !== undefined && <Badge variant={nft.isActive ? 'default' : 'secondary'} className="text-[10px]">{nft.isActive ? 'Active' : 'Inactive'}</Badge>}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="text-xs space-y-1">
          <div className="flex gap-3"><span className="text-muted-foreground w-16">ID</span><span className="font-mono break-all">{nft.id}</span></div>
          {nft.tokenId && <div className="flex gap-3"><span className="text-muted-foreground w-16">Token</span><span className="font-mono">{nft.tokenId}</span></div>}
        </div>
        <Separator />
        <div className="flex flex-wrap gap-2">
          <TransferDialog nftId={nft.id} onDone={onRefresh} />
          <BurnDialog nftId={nft.id} onDone={onRefresh} />
          <Button variant="outline" size="sm" onClick={fetchMeta} disabled={metaLoading}>{metaLoading ? 'Loading…' : 'Metadata'}</Button>
        </div>
        {metaResult !== null && <ResultDisplay result={typeof metaResult === 'string' ? undefined : metaResult} message={typeof metaResult === 'string' ? metaResult : undefined} isError={metaError} />}
        <div>
          <p className="mb-2 text-[11px] font-semibold uppercase text-muted-foreground">Raw</p>
          <div className="rounded-md bg-muted/50 p-3 text-xs"><JsonViewer data={nft} /></div>
        </div>
      </CardContent>
    </Card>
  )
}

// ── Gallery ──

function NftGallery() {
  const [nfts, setNfts] = useState<NftRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<NftRecord | null>(null)

  const fetchNfts = useCallback(async () => {
    setLoading(true); setError(null)
    const res = await azoa.api.request<NftRecord[]>('GET', '/api/nft')
    if (isOk(res)) setNfts(res.value); else setError(res.error.message)
    setLoading(false)
  }, [])

  useEffect(() => { fetchNfts() }, [fetchNfts])

  if (loading) return <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">{[...Array(3)].map((_, i) => <Card key={i} className="animate-pulse"><CardContent className="h-20 pt-6" /></Card>)}</div>
  if (error) return <ResultDisplay message={error} isError />
  if (nfts.length === 0) return <Card><CardContent className="pt-6 text-center text-sm text-muted-foreground">No NFTs yet. Mint one above.</CardContent></Card>

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        {nfts.map(nft => (
          <button key={nft.id} type="button" onClick={() => setSelected(selected?.id === nft.id ? null : nft)} className="text-left">
            <Card className={`cursor-pointer transition-colors hover:border-primary/40 ${selected?.id === nft.id ? 'border-primary/60' : ''}`}>
              <CardHeader className="pb-2"><CardTitle className="text-xs truncate">{nft.name}</CardTitle></CardHeader>
              <CardContent className="flex items-center gap-2 text-xs">
                <ChainBadge chain={nft.chainId} />
                {nft.tokenId && <span className="font-mono text-muted-foreground truncate">{nft.tokenId}</span>}
              </CardContent>
            </Card>
          </button>
        ))}
      </div>
      {selected && <NftDetail nft={selected} onRefresh={fetchNfts} />}
    </div>
  )
}

export default function NftsPage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">NFTs</h1>
        <p className="text-sm text-muted-foreground">Mint, transfer, and burn NFTs</p>
      </div>
      <MintNftForm />
      <div>
        <p className="mb-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Gallery</p>
        <NftGallery />
      </div>
    </div>
  )
}
