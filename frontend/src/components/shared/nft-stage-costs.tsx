'use client'

// Recipient-stage Solana costs (F-M11): the stage's paying participant reviews and accepts an exact offer.
// See `src/components/shared/AGENTS.md` §nft-stage-costs.
import { useEffect, useRef, useState } from 'react'
import type { NftLifecycleView, NftProtocolCostOffer, NftStageAcceptance, NftStageCostAcceptance } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { NftProtocolCostReview } from './nft-protocol-cost-review'

type Saved = { stage: NftStageAcceptance; offer: NftProtocolCostOffer; acceptance?: NftStageCostAcceptance }

/** Shown to the stage's payer only; `costsChanged` is set by the panel when continuing was refused with NFT_STAGE_COSTS_CHANGED. */
export function NftStageCosts({ payer, value, costsChanged, onChange }: {
  payer: string; value: NftLifecycleView; costsChanged: boolean; onChange: (value: NftLifecycleView) => void
}) {
  return <ScopedStageCosts key={`${payer}:${value.operationId}`} payer={payer} value={value} costsChanged={costsChanged} onChange={onChange} />
}

function ScopedStageCosts({ payer, value, costsChanged, onChange }: {
  payer: string; value: NftLifecycleView; costsChanged: boolean; onChange: (value: NftLifecycleView) => void
}) {
  const storageKey = `azoa.nftStageCosts.v1.${payer}:${value.operationId}`
  const [saved, setSaved] = useState<Saved | null>(null)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [accepted, setAccepted] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const alive = useRef(false)
  const inFlight = useRef(false)
  const needsReview = value.needsStageCosts === true || costsChanged

  useEffect(() => {
    alive.current = true
    try {
      const raw = localStorage.getItem(storageKey)
      const restored = raw ? JSON.parse(raw) as Saved : null
      if (restored) {
        if (restored.offer?.quote.lifecycleId !== value.operationId || restored.offer.quote.actorUserId !== payer
          || (restored.acceptance && restored.acceptance.stage.requestId !== restored.stage.requestId))
          throw new Error('Saved stage costs do not match this operation.')
        // An unsent offer for an older version or generation is stale; an acceptance is kept until the node shows its outcome.
        if (restored.acceptance || (restored.stage.expectedVersion === value.version && restored.stage.generation === value.preparationGeneration))
          setSaved(restored)
        else localStorage.removeItem(storageKey)
      }
      setReady(true)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Saved stage costs could not be restored.') }
    return () => { alive.current = false }
  }, [payer, storageKey, value.operationId, value.version, value.preparationGeneration])

  // The node no longer asks for costs: a saved acceptance bound (or the stage moved on), so the saved request is spent.
  useEffect(() => {
    if (ready && saved?.acceptance && !needsReview) {
      localStorage.removeItem(storageKey)
      setSaved(null)
      setAccepted(true)
    }
  }, [ready, saved, needsReview, storageKey])

  const quote = async () => {
    if (!ready || inFlight.current || saved?.acceptance) return
    inFlight.current = true; setBusy(true); setError(null); setAccepted(false)
    try {
      const stage: NftStageAcceptance = { requestId: crypto.randomUUID(), expectedVersion: value.version,
        state: value.state, stage: value.stage, generation: value.preparationGeneration }
      const result = await azoa.api.quoteNftStageCosts(value.operationId, stage)
      if (!alive.current) return
      if (!isOk(result)) { setError(result.error.message); return }
      const offer = result.value
      if (offer.quote.lifecycleId !== value.operationId || offer.quote.actorUserId !== payer
        || offer.quote.preparationGeneration !== stage.generation || offer.quote.stage !== stage.stage)
        throw new Error('The offer does not match the reviewed stage.')
      const next = { stage, offer }
      localStorage.setItem(storageKey, JSON.stringify(next))
      setSaved(next)
    } catch (cause) {
      if (alive.current) setError(cause instanceof Error ? cause.message : 'The cost offer could not be read.')
    } finally {
      inFlight.current = false
      if (alive.current) setBusy(false)
    }
  }

  const accept = async () => {
    if (!ready || inFlight.current || !saved) return
    inFlight.current = true; setBusy(true); setError(null)
    try {
      // Saved before sending: a lost answer is retried with the same request id, which the node treats as the same acceptance.
      const request = saved.acceptance ?? { stage: saved.stage, costs: saved.offer.authorization }
      if (!saved.acceptance) {
        const next = { ...saved, acceptance: request }
        localStorage.setItem(storageKey, JSON.stringify(next))
        setSaved(next)
      }
      const result = await azoa.api.acceptNftStageCosts(value.operationId, request)
      if (!alive.current) return
      if (isOk(result)) {
        localStorage.removeItem(storageKey)
        setSaved(null)
        setAccepted(true)
        onChange(result.value)
      } else setError(result.error.message)
    } catch (cause) {
      if (alive.current) setError(cause instanceof Error ? cause.message : 'Acceptance unresolved. Retry the saved acceptance.')
    } finally {
      inFlight.current = false
      if (alive.current) setBusy(false)
    }
  }

  const discard = () => {
    localStorage.removeItem(storageKey)
    setSaved(null)
    setError(null)
  }

  if (!needsReview && !saved && !accepted) return null
  return <div className="space-y-3 rounded border p-3">
    {costsChanged
      ? <p role="status">The network costs for this stage changed since you accepted them, so nothing was signed. Review and accept the current costs to continue.</p>
      : needsReview && <p>This stage pays network fees and may create the receiving account, and you are its payer. Nothing is signed until you accept the exact costs.</p>}
    {saved && <NftProtocolCostReview offer={saved.offer} accepted={false} heading="Stage costs" />}
    <div className="flex flex-wrap gap-2">
      {needsReview && !saved &&
        <Button disabled={!ready || busy} onClick={() => void quote()}>{costsChanged ? 'Review current stage costs' : 'Review stage costs'}</Button>}
      {saved &&
        <Button disabled={!ready || busy} onClick={() => void accept()}>{saved.acceptance ? 'Retry saved cost acceptance' : 'Accept stage costs'}</Button>}
      {saved && error &&
        <Button variant="outline" disabled={!ready || busy} onClick={discard}>Discard this offer and review a new one</Button>}
    </div>
    {accepted && !needsReview && <p>Stage costs accepted. Continue the lifecycle to prepare and sign this stage.</p>}
    {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
  </div>
}
