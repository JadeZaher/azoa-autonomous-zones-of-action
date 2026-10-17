'use client'

import { useEffect, useRef, useState } from 'react'
import type { NftLifecycleView, NftProtocolCostOffer, NftReplacementCostAcceptance, NftStageAcceptance } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { NftProtocolCostReview } from './nft-protocol-cost-review'

type Saved = { stage: NftStageAcceptance; offer: NftProtocolCostOffer; acceptance?: NftReplacementCostAcceptance }

export function NftReplacementCosts({ actor, value, onChange }: {
  actor: string; value: NftLifecycleView; onChange: (value: NftLifecycleView) => void
}) {
  return <ScopedCosts key={`${actor}:${value.operationId}`} actor={actor} value={value} onChange={onChange} />
}

function ScopedCosts({ actor, value, onChange }: {
  actor: string; value: NftLifecycleView; onChange: (value: NftLifecycleView) => void
}) {
  const storageKey = `azoa.nftReplacementCosts.v1.${actor}:${value.operationId}`
  const [saved, setSaved] = useState<Saved | null>(null)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState('')
  const [error, setError] = useState<string | null>(null)
  const alive = useRef(false)
  const inFlight = useRef(false)
  const settled = status === 'Applied' || status === 'Obsolete'
  const canQuote = value.needsReplacementCosts === true && (!saved?.acceptance || settled)
  useEffect(() => {
    alive.current = true
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) {
        const restored = JSON.parse(raw) as Saved
        if (restored.offer?.quote.lifecycleId !== value.operationId || restored.offer.quote.actorUserId !== actor
          || restored.stage.generation !== restored.offer.quote.preparationGeneration
          || (restored.acceptance && restored.acceptance.stage.requestId !== restored.stage.requestId))
          throw new Error('Saved replacement costs do not match this operation.')
        setSaved(restored)
      }
      setReady(true)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Saved cost request could not be restored.') }
    return () => { alive.current = false }
  }, [actor, storageKey, value.operationId])

  const perform = async (kind: 'quote' | 'accept' | 'reconcile' | 'retire') => {
    if (!ready || inFlight.current) return
    inFlight.current = true; setBusy(true); setError(null)
    try {
      if (kind === 'quote') {
        if (!canQuote) return
        const stage: NftStageAcceptance = { requestId: crypto.randomUUID(), expectedVersion: value.version,
          state: value.state, stage: value.stage, generation: value.preparationGeneration }
        const result = await azoa.api.quoteNftReplacementCosts(value.operationId, stage)
        if (!alive.current) return
        if (!isOk(result)) { setError(result.error.message); return }
        if (result.value.quote.lifecycleId !== value.operationId || result.value.quote.actorUserId !== actor
          || result.value.quote.preparationGeneration !== stage.generation || result.value.quote.stage !== stage.stage)
          throw new Error('The offer does not match the reviewed replacement stage.')
        const next = { stage, offer: result.value }
        localStorage.setItem(storageKey, JSON.stringify(next))
        setSaved(next); setStatus('')
      } else if (saved) {
        let request = saved.acceptance
        if (!request) {
          if (kind !== 'accept') return
          request = { stage: saved.stage, costs: saved.offer.authorization }
          const next = { ...saved, acceptance: request }
          localStorage.setItem(`${storageKey}.${request.stage.requestId}`, JSON.stringify(next))
          localStorage.setItem(storageKey, JSON.stringify(next))
          setSaved(next)
        }
        if (kind === 'accept') {
          const result = await azoa.api.acceptNftReplacementCosts(value.operationId, request)
          if (!alive.current) return
          if (isOk(result)) { setStatus('Applied'); onChange(result.value) }
          else setError(result.error.message)
        } else {
          const result = await (kind === 'retire' ? azoa.api.retireNftReplacementCosts(value.operationId, request)
            : azoa.api.reconcileNftReplacementCosts(value.operationId, request))
          if (!alive.current) return
          if (isOk(result)) { setStatus(result.value.status); onChange(result.value.lifecycle) }
          else setError(result.error.message)
        }
      }
    } catch (cause) {
      if (alive.current) setError(cause instanceof Error ? cause.message : 'Request unresolved. Keep the saved acceptance for recovery.')
    } finally {
      inFlight.current = false
      if (alive.current) setBusy(false)
    }
  }

  if (!value.needsReplacementCosts && !saved && !error) return null
  return <div className="space-y-3 rounded border p-3">
    <p>Review fresh creation costs for the replacement attempt. Accepting costs saves permission; use the lifecycle controls to continue.</p>
    {saved && <NftProtocolCostReview offer={saved.offer} accepted={status === 'Applied'} />}
    <div className="flex flex-wrap gap-2">
      {canQuote &&
        <Button disabled={!ready || busy} onClick={() => perform('quote')}>Review replacement costs</Button>}
      {saved && !settled &&
        <Button disabled={!ready || busy} onClick={() => perform('accept')}>{saved.acceptance ? 'Retry saved cost acceptance' : 'Accept replacement costs'}</Button>}
      {saved?.acceptance && <Button variant="outline" disabled={!ready || busy} onClick={() => perform('reconcile')}>Reconcile cost acceptance</Button>}
      {saved?.acceptance && !settled &&
        <Button variant="outline" disabled={!ready || busy} onClick={() => perform('retire')}>Retire uncommitted cost acceptance</Button>}
    </div>
    {status === 'Applied' && !value.needsReplacementCosts && <p>Replacement costs were accepted. Continue the recorded lifecycle to prepare this attempt.</p>}
    {status === 'Applied' && value.needsReplacementCosts && <p>The saved acceptance covered an earlier attempt. This attempt needs its own cost review.</p>}
    {status === 'Unresolved' && <p>Acceptance remains unresolved. Retry the saved request or retire it before choosing another offer.</p>}
    {status === 'Obsolete' && <p>The old acceptance can no longer commit. Its saved record is retained; review the current replacement costs.</p>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
  </div>
}
