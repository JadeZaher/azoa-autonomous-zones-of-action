'use client'

import { useEffect, useRef, useState } from 'react'
import type { NftLifecycleView, NftWorkflowCostAcceptance, NftWorkflowCostDisclosure } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { NftProtocolCostReview } from '@/components/shared/nft-protocol-cost-review'
import { NftLifecyclePanel } from '@/components/shared/nft-lifecycle-panel'

type Saved = { disclosure: NftWorkflowCostDisclosure; acceptance?: NftWorkflowCostAcceptance }

export function NftWorkflowCosts({ scope, executionId }: { scope: string; executionId: string }) {
  return <ScopedNftWorkflowCosts key={`${scope}:${executionId}`} scope={scope} executionId={executionId} />
}

function ScopedNftWorkflowCosts({ scope, executionId }: { scope: string; executionId: string }) {
  const storageKey = `azoa.nftWorkflowCosts.v1.${scope}:${executionId}`
  const [saved, setSaved] = useState<Saved | null>(null)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [state, setState] = useState('')
  const [lifecycle, setLifecycle] = useState<NftLifecycleView | null>(null)
  const alive = useRef(false)
  const inFlight = useRef(false)
  useEffect(() => {
    alive.current = true
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) {
        const value = JSON.parse(raw) as Saved
        if (value.disclosure.executionId !== executionId || !value.disclosure.offer?.authorization
          || (value.acceptance && value.acceptance.executionId !== executionId)) throw new Error('Saved cost request does not match this execution.')
        setSaved(value)
      }
      setReady(true)
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to restore saved cost request.') }
    return () => { alive.current = false }
  }, [storageKey, executionId])

  const perform = async (kind: 'quote' | 'accept' | 'reconcile' | 'retire') => {
    if (!ready || inFlight.current) return
    inFlight.current = true; setBusy(true); setError(null)
    try {
      if (kind === 'quote') {
        if (saved?.acceptance && state !== 'Obsolete') return
        const result = await azoa.api.quoteNftWorkflowCosts(executionId)
        if (!alive.current) return
        if (!isOk(result)) { setError(result.error.message); return }
        if (result.value.executionId !== executionId) throw new Error('Cost offer does not match this execution.')
        const next = { disclosure: result.value }
        localStorage.setItem(storageKey, JSON.stringify(next))
        setSaved(next); setState(''); setLifecycle(null)
      } else if (saved) {
        let request = saved.acceptance
        if (!request) {
          if (kind !== 'accept') return
          request = { requestId: crypto.randomUUID(), executionId,
            expectedExecutionVersion: saved.disclosure.executionVersion, authorization: saved.disclosure.offer.authorization }
          const next = { ...saved, acceptance: request }
          localStorage.setItem(`${storageKey}.${request.requestId}`, JSON.stringify(next))
          localStorage.setItem(storageKey, JSON.stringify(next))
          setSaved(next)
        }
        if (kind === 'reconcile' || kind === 'retire') {
          const result = await (kind === 'retire' ? azoa.api.retireNftWorkflowCosts(request) : azoa.api.reconcileNftWorkflowCosts(request))
          if (!alive.current) return
          if (isOk(result)) { setState(result.value.state); setLifecycle(result.value.lifecycle ?? null) }
          else setError(result.error.message)
        } else {
          const result = await azoa.api.acceptNftWorkflowCosts(request)
          if (!alive.current) return
          if (isOk(result)) { setState('Applied'); setLifecycle(result.value) }
          else setError(result.error.message)
        }
      }
    } catch (e) {
      if (alive.current) setError(e instanceof Error ? e.message : 'Unable to resolve cost consent. Keep the saved request for recovery.')
    } finally {
      inFlight.current = false
      if (alive.current) setBusy(false)
    }
  }

  return <div className="space-y-3 rounded border p-3">
    <p className="text-sm">Review creation costs for this workflow mint. Accepting costs admits the original mint; completion still requires chain confirmation.</p>
    {saved && <NftProtocolCostReview offer={saved.disclosure.offer} accepted={!!saved.acceptance} />}
    <div className="flex flex-wrap gap-2">
      {(!saved?.acceptance || state === 'Obsolete') && <Button disabled={!ready || busy} onClick={() => perform('quote')}>
        {saved ? 'Review a fresh cost offer' : 'Review creation costs'}</Button>}
      {saved && state !== 'Applied' && state !== 'Obsolete' && <Button disabled={!ready || busy} onClick={() => perform('accept')}>
        {saved.acceptance ? 'Retry saved acceptance' : 'Accept these costs'}</Button>}
      {saved?.acceptance && <Button variant="outline" disabled={!ready || busy} onClick={() => perform('reconcile')}>Reconcile saved acceptance</Button>}
      {saved?.acceptance && state !== 'Applied' && state !== 'Obsolete' &&
        <Button variant="outline" disabled={!ready || busy} onClick={() => perform('retire')}>Retire uncommitted acceptance</Button>}
    </div>
    {saved?.acceptance && <p className="text-xs break-all">Saved acceptance: {saved.acceptance.requestId}</p>}
    {state === 'Unresolved' && <p>Acceptance is unresolved. Reconcile again or retry the same saved request.</p>}
    {state === 'Obsolete' && <p>This request was not accepted before the execution changed. Its saved record is retained; review a fresh offer to continue.</p>}
    {lifecycle && <NftLifecyclePanel value={lifecycle} onChange={setLifecycle} />}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
  </div>
}
