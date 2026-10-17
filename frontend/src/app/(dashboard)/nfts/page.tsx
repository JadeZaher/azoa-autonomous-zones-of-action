'use client'

import { useState, useEffect, useCallback } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import Link from 'next/link'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { ChainBadge } from '@/components/shared/chain-badge'
import { ResultDisplay } from '@/components/shared/result-display'
import { JsonViewer } from '@/components/shared/json-viewer'

interface NftRecord { id: string; name: string; chainId: string; tokenId?: string; isActive?: boolean; [k: string]: unknown }

// ── Mint ──

function NftDetail({ nft }: { nft: NftRecord }) {
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
          <Link href="/user-nfts" className="text-sm underline">Manage transfer, burn and pending operations</Link>
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
      {selected && <NftDetail nft={selected} />}
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
      <Card><CardContent className="pt-6"><Link href="/user-nfts" className="underline">Mint an NFT or resume a transaction</Link><p className="text-sm text-muted-foreground">Track chain confirmation and any required acceptance in the NFT lifecycle screen.</p></CardContent></Card>
      <div>
        <p className="mb-3 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">Gallery</p>
        <NftGallery />
      </div>
    </div>
  )
}
