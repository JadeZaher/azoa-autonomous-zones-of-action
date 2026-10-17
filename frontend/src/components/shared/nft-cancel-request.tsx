'use client'

import { useEffect, useState } from 'react'
import type { NftLifecycleView, NftOperation } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { useNftRequestScope } from './use-nft-request-scope'

export function NftCancelRequest({ operation, requestKey, storageKey, onCancelled }: {
  operation: NftOperation; requestKey: string; storageKey: string; onCancelled: (value: NftLifecycleView) => void
}) {
  const generation = useNftRequestScope(`${storageKey}:${requestKey}`)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setBusy(false); setError(null) }, [storageKey, requestKey])
  const cancel = async () => {
    const current = generation.current
    setBusy(true); setError(null)
    try {
      const result = await azoa.api.cancelNftRequest(operation, requestKey)
      if (generation.current !== current) return
      if (!isOk(result)) { setError(result.error.message); return }
      if (result.value.state !== 'Cancelled') { setError('This request is still active. Reconcile its operation before editing.'); return }
      const saved = localStorage.getItem(storageKey)
      if (saved) { try { if (JSON.parse(saved).key === requestKey) localStorage.removeItem(storageKey) } catch { /* Do not erase a different unreadable request. */ } }
      onCancelled(result.value)
    } finally { if (generation.current === current) setBusy(false) }
  }
  return <div className="space-y-1">
    <Button type="button" variant="outline" disabled={busy} onClick={cancel}>Cancel this request before editing</Button>
    <p className="text-xs text-muted-foreground">The server must confirm cancellation. Pending submitted transactions must be reconciled before cancellation.</p>
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </div>
}
