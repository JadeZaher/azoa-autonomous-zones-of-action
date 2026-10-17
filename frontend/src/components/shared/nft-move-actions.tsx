'use client'

import { useEffect, useRef, useState } from 'react'
import { useNftRequestScope } from './use-nft-request-scope'
import { NftCancelRequest } from './nft-cancel-request'
import { NftFeeReview } from './nft-fee-review'
import type { NftLifecycleView, NftFeeAuthorization, NftFeeQuote } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

export function NftMoveActions({ nftId, chainType, transferable, onStarted }: {
  nftId: string; chainType: string; transferable: boolean; onStarted: (value: NftLifecycleView) => void
}) {
  const { userId } = useAzoaAuth()
  const generation = useNftRequestScope(`${userId}:${nftId}:${chainType}`)
  const [recipient, setRecipient] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [locked, setLocked] = useState(false)
  const [savedRequest, setSavedRequest] = useState<{ key: string; kind: 'burn' | 'transfer' } | null>(null)
  const request = useRef<{ kind: 'transfer' | 'burn'; key: string; recipient: string; operationId?: string; feeAuthorization?: NftFeeAuthorization } | null>(null)
  const [corrupt, setCorrupt] = useState(false)
  const [feeQuote, setFeeQuote] = useState<NftFeeQuote | null>(null)
  useEffect(() => {
    request.current = null; setSavedRequest(null); setLocked(false); setRecipient(''); setCorrupt(false); setError(null); setFeeQuote(null); setBusy(false)
    const stored = localStorage.getItem(`azoa:nft-move:${userId}:${nftId}`)
    if (!stored) return
    try {
      const saved = JSON.parse(stored)
      if (!['transfer', 'burn'].includes(saved.kind) || typeof saved.key !== 'string' || typeof saved.recipient !== 'string') throw new Error('Invalid request')
      request.current = saved; setSavedRequest(saved); setRecipient(saved.recipient); setLocked(true)
    } catch { setCorrupt(true); setError('The saved request needs reconciliation before another request.') }
  }, [userId, nftId, chainType])
  const send = async (kind: 'transfer' | 'burn') => {
    if (!userId || corrupt) return
    const current = generation.current
    const storageKey = `azoa:nft-move:${userId}:${nftId}`
    if (!request.current) {
      const existing = localStorage.getItem(storageKey)
      if (existing) {
        try { request.current = JSON.parse(existing) } catch { setError('The saved request could not be read. Reconcile its operation before retrying.'); return }
      } else {
        const operation = kind === 'burn' ? 'Burn' : 'Transfer'
        if (!feeQuote || feeQuote.operation !== operation || feeQuote.chainType.toLowerCase() !== chainType.toLowerCase() || feeQuote.payerUserId !== userId) {
          setBusy(true); setError(null)
          try { const quote = await azoa.api.getNftFeeQuote(chainType, operation, { nftId }); if (generation.current !== current) return; if (isOk(quote)) setFeeQuote(quote.value); else setError(quote.error.message) }
          finally { if (generation.current === current) setBusy(false) }
          return
        }
        request.current = { kind, key: crypto.randomUUID(), recipient: recipient.trim(), feeAuthorization: { revision: feeQuote.revision, maxNativeBaseUnits: feeQuote.nativeBaseUnits, quoteId: feeQuote.quoteId } }
        localStorage.setItem(storageKey, JSON.stringify(request.current))
      }
    }
    const pending = request.current
    if (!pending || pending.kind !== kind) { setError('A different request is already pending for this NFT. Reconcile it first.'); return }
    setSavedRequest(pending); setLocked(true); setBusy(true); setError(null)
    try {
      const result = kind === 'burn' ? await azoa.api.burnUserNFT(nftId, pending.key, pending.feeAuthorization)
        : await azoa.api.transferUserNFT(nftId, pending.recipient, pending.key, pending.feeAuthorization)
      if (generation.current !== current) return
      if (isOk(result)) {
        pending.operationId = result.value.operationId
        localStorage.setItem(storageKey, JSON.stringify(pending))
        onStarted(result.value)
        if (['Completed', 'Cancelled', 'Failed'].includes(result.value.state)) { localStorage.removeItem(storageKey); request.current = null; setSavedRequest(null); setLocked(false); setFeeQuote(null) }
      } else setError(result.error.message)
    } finally { if (generation.current === current) setBusy(false) }
  }
  return <div className="space-y-2">
    {feeQuote && <NftFeeReview quote={feeQuote} />}
    {transferable && <><Label htmlFor={`recipient-${nftId}`}>Recipient address</Label>
      <Input id={`recipient-${nftId}`} value={recipient} disabled={locked} onChange={event => setRecipient(event.target.value)} />
      <Button disabled={busy || corrupt || !userId || (!locked && !recipient.trim())} onClick={() => send('transfer')}>{locked ? 'Retry transfer' : feeQuote?.operation === 'Transfer' ? 'Authorize fee and transfer' : 'Review transfer fee'}</Button></>}
    <Button variant="destructive" disabled={busy || corrupt || !userId} onClick={() => send('burn')}>{locked ? 'Retry burn' : feeQuote?.operation === 'Burn' ? 'Authorize fee and burn' : 'Review burn fee'}</Button>
    <p className="text-xs text-muted-foreground">Transfers and burns remain pending until confirmed. Some chains require recipient acceptance or creator destruction.</p>
    {locked && savedRequest && <NftCancelRequest operation={savedRequest.kind === 'burn' ? 'Burn' : 'Transfer'} requestKey={savedRequest.key} storageKey={`azoa:nft-move:${userId}:${nftId}`} onCancelled={value => { request.current = null; setSavedRequest(null); setLocked(false); setFeeQuote(null); setError(null); onStarted(value) }} />}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
  </div>
}
