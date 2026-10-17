"use client"

import { useEffect, useRef, useState } from 'react'
import type { FungibleAttachmentView, FungibleCreationView, FungibleMintParams } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ErrorBanner } from '@/components/shared/error-banner'
import { isFungibleCommandSuperseded } from '@/lib/fungible-command-recovery'

type SavedRequest = { key: string; params: FungibleMintParams; operationId?: string }
type Command = { operationId: string; kind: 'continue' | 'replace' | 'cancel' | 'accept'; requestId: string; version: number; quoteId?: string }
type Journal = { requests: SavedRequest[]; commands: Command[] }
const terminal = (state: string) => state === 'Completed' || state === 'Cancelled'

/** Preserves original issuance keys and exact participant commands across lost responses. */
export function FungibleCreationPanel({ actorId }: { actorId: string }) {
  const storageKey = `azoa:fungible-creation:v1:${actorId}`
  const alive = useRef(true)
  const lock = useRef(false)
  const journal = useRef<Journal>({ requests: [], commands: [] })
  const [saved, setSaved] = useState<SavedRequest[]>([])
  const [operations, setOperations] = useState<FungibleCreationView[]>([])
  const [draft, setDraft] = useState({ chainType: 'Algorand', name: 'Project Share', unitName: 'PSHARE', total: '1000000', decimals: '0', holonId: '' })
  const [busy, setBusy] = useState(false)
  const [ready, setReady] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [offset, setOffset] = useState(0)
  const [hasMore, setHasMore] = useState(false)
  const [attachmentId, setAttachmentId] = useState('')
  const [attachment, setAttachment] = useState<FungibleAttachmentView | null>(null)
  const attachmentCommand = useRef<{ requestId: string; version: number; projectionAuthorityId: string } | null>(null)

  function persist(next: Journal) {
    localStorage.setItem(storageKey, JSON.stringify(next))
    journal.current = next
    if (alive.current) setSaved(next.requests)
  }

  function merge(view: FungibleCreationView) {
    if (!alive.current) return
    setOperations(current => [view, ...current.filter(item => item.operationId !== view.operationId)])
  }

  async function discover(nextOffset = 0) {
    const result = await azoa.api.listFungibleCreations(nextOffset, 25)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    setOperations(current => {
      const incoming = new Map(result.value.map(item => [item.operationId, item]))
      return [...result.value, ...current.filter(item => !incoming.has(item.operationId))]
    })
    setOffset(nextOffset)
    setHasMore(result.value.length === 25)
  }

  useEffect(() => {
    alive.current = true
    try {
      const value = localStorage.getItem(storageKey)
      if (value) {
        const decoded = JSON.parse(value) as Journal
        if (!Array.isArray(decoded.requests) || !Array.isArray(decoded.commands)) throw new Error('Saved creation journal is invalid; preserve it before repairing browser storage.')
        journal.current = decoded
        setSaved(decoded.requests)
      }
      setReady(true)
      void discover()
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Browser recovery storage is unavailable.') }
    return () => { alive.current = false }
    // The parent keys this component by authenticated actor, so requests cannot cross accounts.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [storageKey])

  async function run(work: () => Promise<void>) {
    if (lock.current || !ready) return
    lock.current = true
    setBusy(true); setError(null)
    try { await work() }
    catch (failure) { if (alive.current) setError(failure instanceof Error ? failure.message : 'Request failed; the original request remains saved.') }
    finally { lock.current = false; if (alive.current) setBusy(false) }
  }

  async function admit(request: SavedRequest) {
    const result = await azoa.api.fungibleMint(request.params, { idempotencyKey: request.key })
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    const view = result.value.operation
    if (!view) { setError('The response has no durable operation receipt. Replay the saved request; do not assume issuance.'); return }
    persist({ ...journal.current, requests: journal.current.requests.map(item => item.key === request.key ? { ...item, operationId: view.operationId } : item) })
    merge(view)
  }

  async function create() {
    const params: FungibleMintParams = { chainType: draft.chainType, name: draft.name, unitName: draft.unitName,
      total: draft.total, decimals: Number(draft.decimals), ...(draft.holonId ? { holonId: draft.holonId } : {}) }
    if (!/^[1-9][0-9]*$/.test(draft.total) || BigInt(draft.total) > 18446744073709551615n || !Number.isInteger(params.decimals) || params.decimals < 0 || params.decimals > 19)
      throw new Error('Use an exact positive uint64 supply and decimals from 0 through 19.')
    const request = { key: crypto.randomUUID(), params }
    persist({ ...journal.current, requests: [...journal.current.requests, request] })
    await admit(request)
  }

  async function command(view: FungibleCreationView, kind: Command['kind']) {
    const prior = journal.current.commands.find(item => item.operationId === view.operationId)
    if (prior && prior.kind !== kind) throw new Error(`Resolve the saved ${prior.kind} request first; its outcome may be unknown.`)
    const request = prior ?? { operationId: view.operationId, kind, requestId: crypto.randomUUID(), version: view.version,
      ...(kind === 'accept' ? { quoteId: view.quote?.quoteId } : {}) }
    if (kind === 'accept' && !request.quoteId) throw new Error('Load the exact quote before accepting its costs.')
    if (!prior) persist({ ...journal.current, commands: [...journal.current.commands, request] })
    const result = kind === 'continue' ? await azoa.api.continueFungibleCreation(view.operationId, request)
      : kind === 'replace' ? await azoa.api.replaceFungibleCreation(view.operationId, request)
      : kind === 'cancel' ? await azoa.api.cancelFungibleCreation(view.operationId, request)
      : await azoa.api.acceptFungibleCreationQuote(view.operationId, { version: request.version, quoteId: request.quoteId! })
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    persist({ ...journal.current, commands: journal.current.commands.filter(item => item !== request) })
    merge(result.value)
  }

  async function observe(view: FungibleCreationView, quote = false) {
    const result = quote ? await azoa.api.quoteFungibleCreation(view.operationId) : await azoa.api.refreshFungibleCreation(view.operationId)
    if (!alive.current) return
    if (isOk(result)) {
      if (quote && result.value.state === 'AwaitingConsent') {
        const pending = journal.current.commands.find(item => item.operationId === view.operationId && item.kind === 'accept')
        if (pending && result.value.version > pending.version && result.value.quote?.quoteId !== pending.quoteId)
          persist({ ...journal.current, commands: journal.current.commands.filter(item => item !== pending) })
      }
      merge(result.value)
    } else setError(result.error.message)
  }

  async function inspectAttachment() {
    const result = await azoa.api.getFungibleAttachment(attachmentId)
    if (!alive.current) return
    if (isOk(result)) { setAttachment(result.value); attachmentCommand.current = null }
    else setError(result.error.message)
  }

  async function reconcileCommand(view: FungibleCreationView) {
    const pending = journal.current.commands.find(item => item.operationId === view.operationId)
    if (!pending) return
    const result = await azoa.api.getFungibleCreation(pending.operationId)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    merge(result.value)
    if (!isFungibleCommandSuperseded(pending, result.value))
      throw new Error('The server has not fenced this saved version. Preserve and replay the original command.')
    localStorage.setItem(`${storageKey}:resolved-command:${pending.requestId}`, JSON.stringify({ command: pending, observed: result.value }))
    persist({ ...journal.current, commands: journal.current.commands.filter(item => item !== pending) })
  }

  async function recoverSavedOperation(operationId: string) {
    const result = await azoa.api.getFungibleCreation(operationId)
    if (!alive.current) return
    if (isOk(result) && result.value.operationId === operationId) merge(result.value)
    else setError(isOk(result) ? 'The returned operation does not match the saved command.' : result.error.message)
  }

  async function reconcileAttachment() {
    if (!attachment) return
    const key = `${storageKey}:attachment:${attachment.operationId}`
    const raw = localStorage.getItem(key)
    if (!raw) throw new Error('No saved attachment command requires reconciliation.')
    const pending = JSON.parse(raw) as { requestId: string; version: number; projectionAuthorityId: string }
    const result = await azoa.api.getFungibleAttachment(attachment.operationId)
    if (!alive.current) return
    if (!isOk(result)) { setError(result.error.message); return }
    setAttachment(result.value)
    if (result.value.projectionAuthorityId !== pending.projectionAuthorityId
      || !isFungibleCommandSuperseded({ operationId: attachment.operationId, version: pending.version }, result.value))
      throw new Error('The server has not fenced this saved attachment version. Preserve the original command.')
    localStorage.setItem(`${key}:resolved:${pending.requestId}`, JSON.stringify({ command: pending, observed: result.value }))
    localStorage.removeItem(key)
    attachmentCommand.current = null
  }

  async function attach() {
    if (!attachment) return
    const key = `${storageKey}:attachment:${attachment.operationId}`
    const stored = localStorage.getItem(key)
    const request = attachmentCommand.current ?? (stored ? JSON.parse(stored) : { requestId: crypto.randomUUID(), version: attachment.version, projectionAuthorityId: attachment.projectionAuthorityId })
    localStorage.setItem(key, JSON.stringify(request))
    attachmentCommand.current = request
    const result = await azoa.api.attachFungibleCreation(attachment.operationId, request)
    if (!alive.current) return
    if (isOk(result)) { setAttachment(result.value); localStorage.removeItem(key); attachmentCommand.current = null }
    else setError(result.error.message)
  }

  return <section className="space-y-4 rounded-lg border p-4">
    <h2 className="font-semibold">Create and recover fungible assets</h2>
    <p className="text-sm text-muted-foreground">Admission saves one immutable request. Review network costs before signing. Confirmation establishes the asset; a requested Holon attachment may need separate completion.</p>
    <p className="text-sm text-muted-foreground">Reconciliation retires a saved command only when the server has advanced beyond its exact version. It does not cancel effects that were already authorized.</p>
    <div className="grid gap-3 sm:grid-cols-2">{(['chainType', 'name', 'unitName', 'total', 'decimals', 'holonId'] as const).map(field => <div key={field}>
      <Label htmlFor={`fungible-${field}`}>{field === 'total' ? 'Supply in atomic units (exact integer)' : field === 'holonId' ? 'Optional Holon ID' : field}</Label>
      <Input id={`fungible-${field}`} value={draft[field]} onChange={event => setDraft(current => ({ ...current, [field]: event.target.value }))} />
    </div>)}</div>
    <Button disabled={busy || !ready} onClick={() => void run(create)}>Admit a new distinct issuance</Button>
    {saved.length > 0 && <p className="text-sm">Each new admission creates a separate asset intent. Use the saved requests below to recover an earlier request.</p>}
    {error && <ErrorBanner message={error} />}
    {saved.map(request => <div key={request.key} className="flex flex-wrap items-center gap-2 text-sm">
      <span>{request.params.name} - request {request.key} {request.operationId ? `- operation ${request.operationId}` : '- response not yet recovered'}</span>
      <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => admit(request))}>Replay original request</Button>
    </div>)}
    <Button variant="outline" disabled={busy || !ready} onClick={() => void run(() => discover())}>Discover server operations</Button>
    {journal.current.commands.filter(command => !operations.some(view => view.operationId === command.operationId)).map(command =>
      <div key={command.operationId} className="text-sm">
        <span>Saved {command.kind} for operation {command.operationId}</span>
        <Button variant="outline" disabled={busy || !ready} onClick={() => void run(() => recoverSavedOperation(command.operationId))}>Recover saved command operation</Button>
      </div>)}
    {operations.filter(view => view.purpose !== 'PlatformGenesis').map(view => {
      const pending = journal.current.commands.find(item => item.operationId === view.operationId)
      return <article key={view.operationId} className="space-y-2 rounded border p-3 text-sm">
        <p className="font-medium">{view.state} - {view.chainType} / {view.network}</p>
        <p className="break-all">Operation {view.operationId} - revision {view.version}</p>
        {view.assetId && <p>Finalized asset: {view.assetId}</p>}
        {view.transactionId && <p className="break-all">Transaction: {view.transactionId}</p>}
        {view.detail && <p>{view.detail}</p>}
        {view.requestedHolonId && <p>Requested Holon: {view.requestedHolonId}</p>}
        {view.quote && <div className="rounded bg-muted p-2">
          <p>Network fee: {view.quote.networkFeeBaseUnits} atomic {view.quote.currency} units; minimum balance locked: {view.quote.minimumBalanceBaseUnits} atomic units. Currency decimals: {view.quote.decimals}.</p>
          <p className="break-all">Payer: {view.quote.payerAddress}</p><p>Quote expires: {view.quote.expiresAt}</p>
        </div>}
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => observe(view))}>Observe original operation</Button>
          {pending ? <><Button size="sm" disabled={busy} onClick={() => void run(() => command(view, pending.kind))}>Replay saved {pending.kind}</Button>
            <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => reconcileCommand(view))}>Reconcile saved command with server</Button>
            {pending.kind === 'accept' && view.state === 'AwaitingConsent' && <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => observe(view, true))}>Replace unaccepted quote</Button>}</> : <>
            {view.state === 'AwaitingConsent' && <>
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => command(view, 'continue'))}>Renew this operation's authorization</Button>
              <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => observe(view, true))}>Get current cost quote</Button>
              {view.quote && <Button size="sm" disabled={busy} onClick={() => void run(() => command(view, 'accept'))}>Accept this exact cost quote</Button>}
            </>}
            {['Admitted', 'Preparing', 'Prepared'].includes(view.state) && <Button size="sm" disabled={busy} onClick={() => void run(() => command(view, 'continue'))}>Authorize and continue issuance</Button>}
            {view.state === 'FailedNoEffect' && <Button size="sm" disabled={busy} onClick={() => void run(() => command(view, 'replace'))}>Authorize a replacement quote</Button>}
            {['AwaitingConsent', 'Admitted', 'Preparing', 'Prepared', 'FailedNoEffect'].includes(view.state) && <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => command(view, 'cancel'))}>Cancel if no effect was authorized</Button>}
          </>}
        </div>
        {view.state === 'AttachmentPending' && <p>Issuance and reserve accounting are finalized. The current owner must review the original Holon attachment below; do not issue another asset.</p>}
        {terminal(view.state) && <p>This operation is terminal. Its saved request remains available for exact replay.</p>}
      </article>
    })}
    {hasMore && <Button variant="outline" disabled={busy} onClick={() => void run(() => discover(offset + 25))}>Load more operations</Button>}
    <div className="space-y-2 border-t pt-3">
      <h3 className="font-medium">Complete an original Holon attachment</h3>
      <p className="text-sm">Only the current Holon owner in the original tenant can attach its verified issuance. Enter the original operation ID supplied by the issuer.</p>
      <Input aria-label="Attachment operation ID" value={attachmentId} onChange={event => { setAttachmentId(event.target.value); setAttachment(null) }} />
      <Button variant="outline" disabled={busy || !attachmentId} onClick={() => void run(inspectAttachment)}>Inspect authorized attachment</Button>
      {attachment && <div className="text-sm space-y-2"><p>{attachment.state}: asset {attachment.assetId} on {attachment.chainType}/{attachment.network} - Holon {attachment.holonId}</p>
        <Button disabled={busy || attachment.state === 'Completed'} onClick={() => void run(attach)}>Attach this exact finalized issuance</Button>
        <Button variant="outline" disabled={busy} onClick={() => void run(reconcileAttachment)}>Reconcile saved attachment with server</Button></div>}
    </div>
  </section>
}
