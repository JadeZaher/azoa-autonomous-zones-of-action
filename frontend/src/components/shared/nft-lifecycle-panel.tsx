'use client'

import { useEffect, useState } from 'react'
import type { NftLifecycleView, NftStageAcceptance } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useNftRequestScope } from './use-nft-request-scope'
import { NftFeeReview } from './nft-fee-review'
import { NftRefundAction } from './nft-refund-action'
import { NftReplacementCosts } from './nft-replacement-costs'

const descriptions: Record<NftLifecycleView['state'], string> = {
  Preparing: 'Preparing the transaction.', Prepared: 'The transaction is ready for submission.',
  Submitted: 'Submitted to the chain. Confirmation is still pending.',
  NeedsOperator: 'This operation needs reconciliation before it can continue.',
  AwaitingRecipientAcceptance: 'The recipient must authorize acceptance before delivery.',
  AwaitingRefundDestinationAcceptance: 'The original sender must authorize acceptance of the returned NFT.',
  AwaitingRefundAuthorization: 'The current holder must authorize the return to the original sender.',
  AwaitingCreatorDestruction: 'The original creator must authorize destruction of the returned NFT.',
  Confirmed: 'The chain confirmed the transaction. The platform record is being finalized.',
  Completed: 'Completed with confirmed chain evidence.', Cancelled: 'Request cancelled; no NFT effect completed.',
  Failed: 'The operation failed. Review its details before starting another request.',
}

export function NftLifecyclePanel({ value, onChange }: {
  value: NftLifecycleView | null; onChange: (value: NftLifecycleView) => void
}) {
  const { userId } = useAzoaAuth()
  const generation = useNftRequestScope(`${userId}:${value?.operationId ?? ''}`)
  const [reference, setReference] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setReference(''); setBusy(false); setError(null) }, [userId, value?.operationId])
  const run = async (action: 'refresh' | 'accept' | 'cancel' | 'renew' | 'reaccept') => {
    const id = value?.operationId ?? reference.trim()
    if (!id) return
    if (!value && !/^[0-9a-f]{32}$/.test(id)) {
      setError('Enter the 32-character lowercase operation reference.')
      return
    }
    const current = generation.current
    setBusy(true); setError(null)
    try {
      let acceptance: NftStageAcceptance | undefined
      if (action === 'accept') {
        if (!value || !Number.isSafeInteger(value.version) || !Number.isSafeInteger(value.preparationGeneration))
          throw new Error('Refresh the operation before accepting this stage.')
        const original = value.stageAcceptance
        const storageKey = `azoa:nft-stage-acceptance:${userId}:${id}:${original?.expectedVersion ?? value.version}`
        const saved = localStorage.getItem(storageKey)
        acceptance = saved ? JSON.parse(saved) as NftStageAcceptance : original ?? {
          requestId: crypto.randomUUID(), expectedVersion: value.version, state: value.state,
          stage: value.stage, generation: value.preparationGeneration,
        }
        if (!acceptance.requestId || (original ? acceptance.requestId !== original.requestId
          || acceptance.expectedVersion !== original.expectedVersion || acceptance.state !== original.state
          || acceptance.stage !== original.stage || acceptance.generation !== original.generation
          : acceptance.expectedVersion !== value.version || acceptance.state !== value.state || acceptance.stage !== value.stage
            || acceptance.generation !== value.preparationGeneration))
          throw new Error('Saved acceptance differs from this stage. Refresh the operation to reconcile it.')
        localStorage.setItem(storageKey, JSON.stringify(acceptance))
      }
      if (action === 'renew' || action === 'reaccept') {
        if (!value || !Number.isSafeInteger(value.version) || !Number.isSafeInteger(value.preparationGeneration))
          throw new Error('Refresh the operation before renewing authority.')
        const storageKey = `azoa:${action === 'renew' ? 'nft-actor-renewal' : 'nft-participant-renewal'}:${userId}:${id}:${value.version}`
        const saved = localStorage.getItem(storageKey)
        acceptance = saved ? JSON.parse(saved) as NftStageAcceptance : {
          requestId: crypto.randomUUID(), expectedVersion: value.version, state: value.state,
          stage: value.stage, generation: value.preparationGeneration,
        }
        if (!acceptance.requestId || acceptance.expectedVersion !== value.version || acceptance.state !== value.state
          || acceptance.stage !== value.stage || acceptance.generation !== value.preparationGeneration)
          throw new Error('Saved renewal differs from this stage. Refresh the operation to reconcile it.')
        localStorage.setItem(storageKey, JSON.stringify(acceptance))
      }
      const result = await (action === 'accept' || action === 'reaccept' ? azoa.api.acceptNftLifecycle(id, acceptance!)
        : action === 'renew' ? azoa.api.renewNftLifecycleAuthority(id, acceptance!)
        : action === 'cancel' ? azoa.api.cancelNftLifecycle(id) : azoa.api.refreshNftLifecycle(id))
      if (generation.current !== current) return
      if (isOk(result)) onChange(result.value)
      else setError(result.error.message)
    } catch (cause) {
      if (generation.current === current) setError(cause instanceof Error ? cause.message : 'NFT request could not be completed.')
    } finally { if (generation.current === current) setBusy(false) }
  }
  return <Card>
    <CardHeader><CardTitle className="text-sm">NFT transaction status</CardTitle></CardHeader>
    <CardContent className="space-y-3" aria-live="polite">
      {!value && <div className="space-y-1">
        <Label htmlFor="nft-operation">Resume an operation</Label>
        <Input id="nft-operation" value={reference} onChange={event => setReference(event.target.value)} placeholder="Operation reference" />
      </div>}
      {value && <>
        <p>{descriptions[value.state] ?? 'An unrecognized state requires reconciliation.'}</p>
        <dl className="text-sm space-y-1">
          <dt>Operation reference</dt><dd className="font-mono break-all">{value.operationId}</dd>
          <dt>Operation</dt><dd>{value.operation}</dd>
          <dt>Stage</dt><dd>{value.stage}</dd>
          {value.assetId && <><dt>Asset</dt><dd className="break-all">{value.assetId}</dd></>}
          {value.transactionId && <><dt>Transaction</dt><dd className="font-mono break-all">{value.transactionId}</dd></>}
        </dl>
        {value.feeQuote && <NftFeeReview quote={value.feeQuote} authorization={false} />}
        {value.detail && <p className="text-sm">{value.detail}</p>}
        {value.needsActorContinuation && userId !== value.actorUserId &&
          <p className="text-sm">The requester&apos;s authority has lapsed; this stage continues after they renew it.</p>}
        {value.needsParticipantRenewal && userId !== value.renewalParticipantUserId &&
          <p className="text-sm">The accepting participant&apos;s authority has lapsed; this stage continues after they renew their acceptance.</p>}
      </>}
      <div className="flex gap-2 flex-wrap">
        <Button onClick={() => run('refresh')} disabled={busy || (!value && !reference.trim())}>Refresh status</Button>
        {value?.needsActorContinuation && userId === value.actorUserId &&
          <Button disabled={busy} onClick={() => run('renew')}>Renew my authority</Button>}
        {value?.needsParticipantRenewal && userId === value.renewalParticipantUserId &&
          <Button disabled={busy} onClick={() => run('reaccept')}>Renew my acceptance</Button>}
        {value?.state === 'Preparing' && value.replacementCostsAccepted && userId === value.actorUserId &&
          <Button disabled={busy} onClick={() => run('refresh')}>Continue accepted replacement</Button>}
        {value && (['AwaitingRecipientAcceptance', 'AwaitingCreatorDestruction', 'AwaitingRefundAuthorization', 'AwaitingRefundDestinationAcceptance'].includes(value.state)
          || value.state === 'Preparing' && (value.stageAcceptance || value.needsStageAcceptance)) &&
          <Button disabled={busy} onClick={() => run('accept')}>
            {value.state === 'Preparing' ? value.needsStageAcceptance ? 'Authorize replacement stage' : 'Resume accepted stage'
              : value.state === 'AwaitingCreatorDestruction' ? 'Authorize destruction' : value.state === 'AwaitingRefundAuthorization' ? 'Authorize return' : 'Accept NFT'}
          </Button>}
        {value && ['Preparing', 'Prepared', 'AwaitingRecipientAcceptance'].includes(value.state) &&
          <Button variant="outline" disabled={busy} onClick={() => run('cancel')}>Cancel if unsent</Button>}
      </div>
      {value && <NftRefundAction key={value.operationId} value={value} onChange={onChange} />}
      {value && userId === value.actorUserId && <NftReplacementCosts actor={userId} value={value} onChange={onChange} />}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    </CardContent>
  </Card>
}
