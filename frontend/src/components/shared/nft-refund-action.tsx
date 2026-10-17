'use client'

import { useEffect, useState } from 'react'
import type { NftFeeQuote, NftLifecycleView, NftFeeAuthorization } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Button } from '@/components/ui/button'
import { useNftRequestScope } from './use-nft-request-scope'
import { NftCancelRequest } from './nft-cancel-request'
import { NftFeeReview } from './nft-fee-review'

export function NftRefundAction({ value, onChange }: { value: NftLifecycleView; onChange: (value: NftLifecycleView) => void }) {
  const { userId } = useAzoaAuth()
  const generation = useNftRequestScope(`${userId}:${value.operationId}`)
  const [requestKey, setRequestKey] = useState<string | null>(null)
  const storageKey = `azoa:nft-refund:${userId}:${value.operationId}`
  const [quote, setQuote] = useState<NftFeeQuote | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setQuote(null); setBusy(false); setError(null); setRequestKey(null)
    const saved = localStorage.getItem(storageKey)
    if (saved) { try { const parsed = JSON.parse(saved); if (typeof parsed.key === 'string') setRequestKey(parsed.key) } catch { setError('The saved return request needs reconciliation.') } } }, [userId, value.operationId, storageKey])
  if (value.operation !== 'Transfer' || value.state !== 'Completed' || value.actorUserId !== userId || !value.feeQuote) return null
  const requestReturn = async () => {
    const current = generation.current
    setBusy(true); setError(null)
    try {
      let pending: { key: string; feeAuthorization: NftFeeAuthorization } | null = null
      const stored = localStorage.getItem(storageKey)
      if (stored) {
        try { pending = JSON.parse(stored); if (!pending?.key || !pending.feeAuthorization) throw new Error('Invalid request') }
        catch { setError('The saved return request needs reconciliation.'); return }
      }
      if (!pending && !quote) {
        const result = await azoa.api.getNftFeeQuote(value.feeQuote!.chainType, 'Refund', { sourceOperationId: value.operationId })
        if (generation.current !== current) return
        if (isOk(result)) setQuote(result.value); else setError(result.error.message)
        return
      }
      pending ??= { key: crypto.randomUUID(), feeAuthorization: { revision: quote!.revision, maxNativeBaseUnits: quote!.nativeBaseUnits, quoteId: quote!.quoteId } }
      localStorage.setItem(storageKey, JSON.stringify(pending)); setRequestKey(pending.key)
      const result = await azoa.api.refundNftTransfer(value.operationId, pending.key, pending.feeAuthorization)
      if (generation.current !== current) return
      if (isOk(result)) onChange(result.value); else setError(result.error.message)
    } finally { if (generation.current === current) setBusy(false) }
  }
  return <div className="space-y-2">
    <p className="text-sm">Request this NFT back from its current holder. The holder must authorize the return.</p>
    {quote && <NftFeeReview quote={quote} />}
    <Button variant="outline" disabled={busy} onClick={requestReturn}>{quote ? 'Authorize fee and request return' : 'Review return fee'}</Button>
    {requestKey && <NftCancelRequest operation="Refund" requestKey={requestKey} storageKey={storageKey} onCancelled={result => { setRequestKey(null); setQuote(null); onChange(result) }} />}
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </div>
}
