'use client'

import { useCallback, useEffect, useState } from 'react'
import type { NftLifecycleView } from 'azoa-sdk'
import { useNftRequestScope } from './use-nft-request-scope'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'

export function NftPendingOperations({ userId, onSelect }: { userId: string; onSelect: (value: NftLifecycleView) => void }) {
  const generation = useNftRequestScope(userId)
  const [owner, setOwner] = useState(userId)
  const [items, setItems] = useState<NftLifecycleView[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const load = useCallback(async (after?: string) => {
    const current = generation.current
    setBusy(true); setError(null)
    try {
      const result = await azoa.api.listNftLifecycles(after)
      if (generation.current !== current) return
      if (isOk(result)) {
        setOwner(userId)
        setItems(previous => after ? [...previous.filter(item => !result.value.items.some(next => next.operationId === item.operationId)), ...result.value.items] : result.value.items)
        setCursor(result.value.nextCursor)
      } else setError(result.error.message)
    } finally { if (generation.current === current) setBusy(false) }
  }, [generation, userId])
  useEffect(() => { setItems([]); setCursor(null); setOwner(userId); void load() }, [load, userId])
  return <Card>
    <CardHeader><CardTitle className="text-sm">Pending operations for you</CardTitle></CardHeader>
    <CardContent className="space-y-3">
      <p className="text-sm text-muted-foreground">Includes operations awaiting your acceptance, even when another person started them.</p>
      {owner === userId && items.map(item => <Button key={item.operationId} variant="outline" onClick={() => { if (owner === userId) onSelect(item) }} className="w-full justify-start">
        {item.operation} / {item.state} / {item.nftId}
      </Button>)}
      {!busy && items.length === 0 && <p className="text-sm">No pending operations on this page.</p>}
      <div className="flex gap-2"><Button variant="outline" disabled={busy} onClick={() => load()}>Reload operations</Button>
        {owner === userId && cursor && <Button disabled={busy} onClick={() => load(cursor)}>Load more</Button>}</div>
      {error && <p role="alert" className="text-destructive">{error}</p>}
    </CardContent>
  </Card>
}
