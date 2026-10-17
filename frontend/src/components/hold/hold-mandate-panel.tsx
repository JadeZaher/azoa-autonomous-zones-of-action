'use client'

import { useEffect, useRef, useState } from 'react'
import type { HoldMandateView, HoldRecoveryCommand, HoldMandateResolution } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'

type MandateRequestKind = 'MandateRevoke' | 'MandateReauthorize'
type SavedMandateRequest = { mandateId: string; kind: MandateRequestKind; request: HoldRecoveryCommand }

const actionLabels: Record<MandateRequestKind, string> = { MandateRevoke: 'Revoke automatic release', MandateReauthorize: 'Renew release authority' }
const reasons: Record<MandateRequestKind, string> = {
  MandateRevoke: 'Owner revoked further automatic-release authorization.',
  MandateReauthorize: 'Owner renewed transfer authority for this exact release.',
}

/** Automatic-release mandates; every revoke or renewal is journaled locally before it is sent. */
export function HoldMandatePanel({ scope }: { scope: string }) {
  return <ScopedHoldMandatePanel key={scope} scope={scope} />
}

function ScopedHoldMandatePanel({ scope }: { scope: string }) {
  const key = `azoa.holdMandates.v1.${scope}`
  const [rows, setRows] = useState<HoldMandateView[]>([])
  const [saved, setSaved] = useState<SavedMandateRequest[]>([])
  const journal = useRef<SavedMandateRequest[]>([])
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [nextOffset, setNextOffset] = useState<number | null>(null)
  const alive = useRef(false)
  const locked = useRef(false)

  function merge(row: HoldMandateView) {
    if (alive.current) setRows(current => [row, ...current.filter(value => value.id !== row.id)])
  }
  function persist(requests: SavedMandateRequest[]) {
    localStorage.setItem(key, JSON.stringify(requests))
    journal.current = requests
    if (alive.current) setSaved(requests)
  }
  function archive(pending: SavedMandateRequest) {
    localStorage.setItem(`${key}.${pending.request.requestId}`, JSON.stringify(pending))
    persist(journal.current.filter(value => value.request.requestId !== pending.request.requestId))
  }
  async function discover(offset = 0) {
    const result = await azoa.api.listHoldReleaseMandates(offset, 25)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    for (const row of result.value.items) merge(row)
    setNextOffset(result.value.nextOffset ?? null)
  }
  useEffect(() => {
    alive.current = true
    try {
      const stored = JSON.parse(localStorage.getItem(key) ?? '[]') as SavedMandateRequest[]
      if (!Array.isArray(stored) || stored.some(value => !value.mandateId || !value.request?.requestId)) throw new Error('Saved release requests are invalid. Preserve browser storage for recovery.')
      journal.current = stored; setSaved(stored); setReady(true)
      void discover().catch(failure => { if (alive.current) setError(String(failure)) })
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Recovery storage is unavailable.') }
    return () => { alive.current = false }
    // Scope changes remount this component, including its asynchronous response guards.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])

  async function run(work: () => Promise<void>) {
    if (!ready || locked.current) return
    locked.current = true; setBusy(true); setError(null)
    try { await work() } catch (failure) {
      if (alive.current) setError(failure instanceof Error ? failure.message : 'Keep the saved request and reconcile its outcome.')
    } finally { locked.current = false; if (alive.current) setBusy(false) }
  }
  async function send(pending: SavedMandateRequest) {
    const result = pending.kind === 'MandateRevoke'
      ? await azoa.api.revokeHoldReleaseMandate(pending.mandateId, pending.request)
      : await azoa.api.reauthorizeHoldReleaseMandate(pending.mandateId, pending.request)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    if (result.value.id !== pending.mandateId) throw new Error('Response does not match the saved release mandate.')
    merge(result.value); archive(pending)
  }
  async function reconcile(pending: SavedMandateRequest) {
    const result = await azoa.api.reconcileHoldReleaseMandate(pending.mandateId, { kind: pending.kind, request: pending.request })
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    const value: HoldMandateResolution = result.value
    if (value.mandate && value.mandate.id !== pending.mandateId) throw new Error('Recovery evidence belongs to another release mandate.')
    if (value.mandate) merge(value.mandate)
    if (value.state === 'Applied' || value.state === 'Obsolete') archive(pending)
    else setError('This request remains unresolved. Retry its exact saved request or reconcile again.')
  }
  async function create(row: HoldMandateView, kind: MandateRequestKind) {
    const pending: SavedMandateRequest = { mandateId: row.id, kind, request: { requestId: crypto.randomUUID(), version: row.version, reason: reasons[kind] } }
    localStorage.setItem(`${key}.${pending.request.requestId}`, JSON.stringify(pending))
    persist([...journal.current, pending])
    await send(pending)
  }

  return <section className="space-y-4">
    <h2 className="text-base font-semibold">Automatic release</h2>
    <p>Each release runs only within the terms you accepted. Revoking stops further authorization; an already signed release may still settle and will be reconciled.</p>
    <Button disabled={!ready || busy} onClick={() => void run(() => discover())}>Refresh release mandates</Button>
    {saved.map(request => <div key={request.request.requestId} className="rounded border p-3 space-y-2">
      <p className="text-sm break-all">Saved {request.kind}: {request.mandateId}</p>
      <Button disabled={busy} onClick={() => void run(() => send(request))}>Retry saved {request.kind}</Button>{' '}
      <Button disabled={busy} variant="outline" onClick={() => void run(() => reconcile(request))}>Reconcile saved {request.kind}</Button>
    </div>)}
    {rows.map(row => <article key={row.id} className="rounded border p-4 space-y-2">
      <p className="font-medium">{row.terms.quantityBaseUnits} base units of {row.terms.asset.assetId} - {row.status}</p>
      <p className="text-sm">Release to {row.terms.recipient.address}, after {row.terms.notBefore} and before {row.terms.expiresAt}.</p>
      <p className="text-sm">Maximum native atomic units: network {row.terms.maximumCosts.networkFeeBaseUnits}, locked balance {row.terms.maximumCosts.lockedBalanceBaseUnits}, nonrefundable protocol cost {row.terms.maximumCosts.nonrefundableProtocolCostBaseUnits}.</p>
      <p className="text-xs break-all">Mandate {row.id}. Liability {row.terms.holdRecordId}</p>
      {row.detail && <p>{row.detail}</p>}
      {row.status === 'Active' && <div className="flex flex-wrap gap-2">
        <Button disabled={busy} variant="outline" onClick={() => void run(() => create(row, 'MandateReauthorize'))}>{actionLabels.MandateReauthorize}</Button>
        <Button disabled={busy} variant="destructive" onClick={() => void run(() => create(row, 'MandateRevoke'))}>{actionLabels.MandateRevoke}</Button>
      </div>}
    </article>)}
    {nextOffset !== null && <Button disabled={busy} onClick={() => void run(() => discover(nextOffset))}>Load more release mandates</Button>}
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </section>
}
