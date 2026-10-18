"use client"

import { useEffect, useRef, useState } from 'react'
import type { FungibleCreationView, PlatformGenesisRequest, PlatformGenesisResponse, PlatformTokenState } from 'azoa-sdk'
import { operatorRequest } from '@/lib/operator-client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { isFungibleCommandSuperseded } from '@/lib/fungible-command-recovery'

type SavedCommand = { id: string; kind: 'continue' | 'replace' | 'accept-quote'; body: { requestId?: string; version: number; quoteId?: string } }
type Journal = { definition?: PlatformGenesisRequest; operationId?: string; command?: SavedCommand }

const OPERATION_ID = /^[0-9a-f]{32}$/i
const REQUEST_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const QUOTE_ID = /^[0-9a-f]{64}$/
const COMMAND_KINDS: readonly SavedCommand['kind'][] = ['continue', 'replace', 'accept-quote']

/** Resume state lives in the page URL (no browser storage on operator pages): the operation and any unacknowledged command. */
function readJournalFromUrl(search: string): Journal {
  const params = new URLSearchParams(search)
  const operationId = params.get('operation') ?? ''
  if (!OPERATION_ID.test(operationId)) return {}
  const kind = params.get('command') as SavedCommand['kind'] | null
  const version = Number(params.get('version'))
  const requestId = params.get('request') ?? ''
  const quoteId = params.get('quote') ?? ''
  if (!kind || !COMMAND_KINDS.includes(kind) || !params.has('version') || !Number.isSafeInteger(version) || version < 0) return { operationId }
  if (kind === 'accept-quote') return QUOTE_ID.test(quoteId) ? { operationId, command: { id: operationId, kind, body: { version, quoteId } } } : { operationId }
  return REQUEST_ID.test(requestId) ? { operationId, command: { id: operationId, kind, body: { version, requestId } } } : { operationId }
}

function writeJournalToUrl(journal: Journal) {
  const url = new URL(window.location.href)
  for (const key of ['operation', 'command', 'version', 'request', 'quote']) url.searchParams.delete(key)
  const operationId = journal.command?.id ?? journal.operationId
  if (operationId) url.searchParams.set('operation', operationId)
  if (journal.command) {
    url.searchParams.set('command', journal.command.kind)
    url.searchParams.set('version', String(journal.command.body.version))
    if (journal.command.body.requestId) url.searchParams.set('request', journal.command.body.requestId)
    if (journal.command.body.quoteId) url.searchParams.set('quote', journal.command.body.quoteId)
  }
  window.history.replaceState(window.history.state, '', url)
}

export default function PlatformGenesisPage() {
  const journal = useRef<Journal>({})
  const alive = useRef(true)
  const lock = useRef(false)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState<Journal>({})
  const [state, setState] = useState<PlatformTokenState | null>(null)
  const [operation, setOperation] = useState<FungibleCreationView | null>(null)
  const [draft, setDraft] = useState({ chainType: 'Algorand', name: 'Arda', unitName: 'ARDA', total: '2000000000', decimals: '2', expectedNetwork: 'Devnet' })
  const [confirmed, setConfirmed] = useState(false)
  const [lookup, setLookup] = useState('')

  function persist(next: Journal) {
    writeJournalToUrl(next)
    journal.current = next
    if (alive.current) setSaved(next)
  }

  async function run(work: () => Promise<void>) {
    if (lock.current || !ready) return
    lock.current = true; setBusy(true); setError(null)
    try { await work() }
    catch (failure) { if (alive.current) setError(failure instanceof Error ? failure.message : 'The original request remains saved. Sign in again if required, then replay it.') }
    finally { lock.current = false; if (alive.current) setBusy(false) }
  }

  useEffect(() => {
    alive.current = true
    journal.current = readJournalFromUrl(window.location.search)
    setSaved(journal.current)
    setLookup(journal.current.operationId ?? '')
    setReady(true)
    return () => { alive.current = false }
  }, [])

  async function admit() {
    const request = journal.current.definition ?? { ...draft, decimals: Number(draft.decimals), confirmIrreversible: true as const }
    if (!journal.current.definition && !confirmed) throw new Error('Confirm the immutable definition first.')
    persist({ ...journal.current, definition: request })
    const result = await operatorRequest<PlatformGenesisResponse>('platform-token/genesis', { method: 'POST', body: request })
    if (!alive.current) return
    if (result.operation) {
      persist({ ...journal.current, operationId: result.operation.operationId })
      setOperation(result.operation); setLookup(result.operation.operationId)
    } else if (result.state === 'Completed') {
      const verified = await operatorRequest<PlatformTokenState>('platform-token')
      if (alive.current) setState(verified)
    } else throw new Error('The node returned no durable genesis operation; replay the original definition.')
  }

  async function observe(id = operation?.operationId ?? lookup) {
    if (!/^[0-9a-f]{32}$/i.test(id)) throw new Error('Enter the original operation ID.')
    const result = await operatorRequest<FungibleCreationView>(`platform-token/creation/${id}/refresh`, { method: 'POST', body: {} })
    if (!alive.current) return
    setOperation(result); persist({ ...journal.current, operationId: result.operationId })
  }

  async function quote() {
    if (!operation) return
    const result = await operatorRequest<FungibleCreationView>(`platform-token/creation/${operation.operationId}/quote`, { method: 'POST', body: {} })
    if (alive.current) {
      const pending = journal.current.command
      if (pending?.kind === 'accept-quote' && result.state === 'AwaitingConsent'
        && result.version > pending.body.version && result.quote?.quoteId !== pending.body.quoteId)
        persist({ ...journal.current, command: undefined })
      setOperation(result)
    }
  }

  async function command(kind: SavedCommand['kind']) {
    if (!operation) return
    const prior = journal.current.command
    if (prior && (prior.id !== operation.operationId || prior.kind !== kind)) throw new Error('Replay the saved command before authorizing another step.')
    const request = prior ?? { id: operation.operationId, kind, body: kind === 'accept-quote'
      ? { version: operation.version, quoteId: operation.quote?.quoteId }
      : { version: operation.version, requestId: crypto.randomUUID() } }
    if (kind === 'accept-quote' && !request.body.quoteId) throw new Error('Load the exact quote before accepting costs.')
    persist({ ...journal.current, command: request })
    const result = await operatorRequest<FungibleCreationView>(`platform-token/creation/${request.id}/${request.kind}`, { method: 'POST', body: request.body })
    if (!alive.current) return
    persist({ ...journal.current, command: undefined }); setOperation(result)
  }

  async function reconcileCommand() {
    const pending = journal.current.command
    if (!pending) return
    const observed = await operatorRequest<FungibleCreationView>(`platform-token/creation/${pending.id}`)
    if (!alive.current) return
    setOperation(observed)
    if (!isFungibleCommandSuperseded({ operationId: pending.id, version: pending.body.version }, observed))
      throw new Error('The server has not fenced this saved version. Preserve and replay the original command.')
    persist({ ...journal.current, command: undefined })
  }

  return <main className="space-y-5">
    <h1 className="text-2xl font-semibold">Platform genesis</h1>
    <p>One immutable definition is allowed per network. Admission does not sign. Costs require separate consent, and platform activation follows confirmed issuance and reserve accounting.</p>
    <p>Saved-command reconciliation requires a later server revision. It does not withdraw an authorization that already committed.</p>
    <p className="text-sm text-muted-foreground">Resume state (the operation and any unacknowledged command) is kept in this page&apos;s address, never in browser storage. Keep this tab or copy its URL before navigating away.</p>
    <Button variant="outline" disabled={busy || !ready} onClick={() => void run(async () => { const value = await operatorRequest<PlatformTokenState>('platform-token'); if (alive.current) setState(value) })}>Read verified platform supply</Button>
    {state && <div className="rounded border p-3"><p>Verified asset {state.definition.assetId} - {state.definition.chainType} / {state.definition.network}</p><p>Total {state.supply.total}; circulating {state.supply.circulating}; in reserve {state.supply.inReserve} atomic units.</p></div>}
    <section className="space-y-3 rounded border p-4">
      <h2 className="font-semibold">Freeze or replay the original definition</h2>
      <div className="grid gap-3 sm:grid-cols-2">{(['chainType', 'name', 'unitName', 'total', 'decimals', 'expectedNetwork'] as const).map(field => <div key={field}>
        <Label htmlFor={`genesis-${field}`}>{field === 'total' ? 'Total atomic units (exact integer)' : field}</Label>
        <Input id={`genesis-${field}`} disabled={!!saved.definition || busy} value={saved.definition ? String(saved.definition[field]) : draft[field]}
          onChange={event => setDraft(current => ({ ...current, [field]: event.target.value }))} />
      </div>)}</div>
      {!saved.definition && <label className="flex min-h-11 items-center gap-2"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />I understand supply, decimals, name and network cannot be changed after this definition is claimed.</label>}
      <Button disabled={busy || !ready || (!saved.definition && !confirmed)} onClick={() => void run(admit)}>{saved.definition ? 'Replay original genesis definition' : 'Claim definition and inspect costs'}</Button>
    </section>
    {error && <p role="alert" className="text-destructive">{error}</p>}
    <div className="flex flex-wrap gap-2"><Input aria-label="Original genesis operation ID" value={lookup} onChange={event => setLookup(event.target.value)} />
      <Button variant="outline" disabled={busy || !lookup || !ready} onClick={() => void run(() => observe(lookup))}>Recover original operation</Button></div>
    {operation && <section className="space-y-3 rounded border p-4">
      <h2 className="font-semibold">{operation.state} - {operation.chainType} / {operation.network}</h2>
      <p className="break-all">Operation {operation.operationId} - revision {operation.version}</p>
      <p className="break-all">Reserve: {operation.walletAddress}</p>
      {operation.assetId && <p>Finalized asset {operation.assetId}</p>}
      {operation.transactionId && <p className="break-all">Transaction {operation.transactionId}</p>}
      {operation.detail && <p>{operation.detail}</p>}
      {operation.quote && <div className="rounded bg-muted p-3"><p>Network fee: {operation.quote.networkFeeBaseUnits} atomic {operation.quote.currency}; minimum balance locked: {operation.quote.minimumBalanceBaseUnits} atomic units (currency decimals {operation.quote.decimals}).</p>
        <p className="break-all">Payer {operation.quote.payerAddress}</p><p>Quote expires {operation.quote.expiresAt}</p></div>}
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" disabled={busy} onClick={() => void run(() => observe())}>Observe authorized wire</Button>
        {saved.command ? <><Button disabled={busy} onClick={() => void run(() => command(saved.command!.kind))}>Replay saved {saved.command.kind}</Button>
          <Button variant="outline" disabled={busy} onClick={() => void run(reconcileCommand)}>Reconcile saved command with server</Button>
          {saved.command.kind === 'accept-quote' && operation.state === 'AwaitingConsent' && <Button variant="outline" disabled={busy} onClick={() => void run(quote)}>Replace unaccepted quote</Button>}</> : <>
          {operation.state === 'AwaitingConsent' && <>
            <Button variant="outline" disabled={busy} onClick={() => void run(() => command('continue'))}>Renew this operation's operator authority</Button>
            <Button variant="outline" disabled={busy} onClick={() => void run(quote)}>Request cost quote</Button>
            {operation.quote && <Button disabled={busy} onClick={() => void run(() => command('accept-quote'))}>Accept exact displayed costs</Button>}
          </>}
          {['Admitted', 'Preparing', 'Prepared'].includes(operation.state) && <Button disabled={busy} onClick={() => void run(() => command('continue'))}>Authorize and continue genesis</Button>}
          {operation.state === 'FailedNoEffect' && <Button disabled={busy} onClick={() => void run(() => command('replace'))}>Authorize replacement with a new quote</Button>}
        </>}
      </div>
      <p className="text-sm">To pause, withhold continuation. There is no permanent genesis cancellation: already-authorized bytes may still settle. Observing does not request new signatures or faucet funding. Fund the original reserve's displayed fees and minimum balance explicitly.</p>
    </section>}
  </main>
}
