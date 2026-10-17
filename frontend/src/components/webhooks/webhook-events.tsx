"use client"

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { WebhookEventStatus, ConsentWebhookEventStatus, WebhookEventRetryCommand, WebhookEventRetryReceipt } from 'azoa-sdk'
import { azoa } from '@/lib/azoa'
import { operatorRequest } from '@/lib/operator-client'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

type DeliveryEvent = WebhookEventStatus | ConsentWebhookEventStatus
type PanelProps = { userId: string; operatorTenantId?: string; family?: 'action' | 'consent' }

type SavedRetry = { eventId: string; command: WebhookEventRetryCommand }
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

function parseSaved(raw: string): SavedRetry {
  const value = JSON.parse(raw) as SavedRetry
  if (!value || !uuid.test(value.eventId) || !value.command || !uuid.test(value.command.requestId)
    || !Number.isSafeInteger(value.command.expectedVersion) || value.command.expectedVersion < 0
    || typeof value.command.reason !== 'string' || value.command.reason.trim().length < 10
    || value.command.reason.length > 1000) throw new Error('The saved retry is invalid. Preserve it for support before changing browser storage.')
  return value
}

export function WebhookEventsPanel({ userId, operatorTenantId, family = 'action' }: PanelProps) {
  return <EventList key={`${userId}:${operatorTenantId ?? ''}:${family}`} userId={userId} operatorTenantId={operatorTenantId} family={family} />
}

function EventList({ userId, operatorTenantId, family = 'action' }: PanelProps) {
  const storageKey = `azoa:${family === 'consent' ? 'consent-' : ''}webhook-retry:${userId}:${operatorTenantId ?? 'self'}`
  const api = useMemo(() => {
    if (operatorTenantId) {
      const path = `tenants/${encodeURIComponent(operatorTenantId)}/${family === 'consent' ? 'consent-webhook-events' : 'webhook-events'}`
      return {
        list: (offset: number) => operatorRequest<DeliveryEvent[]>(`${path}?offset=${offset}&limit=25`),
        get: (id: string) => operatorRequest<DeliveryEvent>(`${path}/${encodeURIComponent(id)}`),
        retry: (id: string, command: WebhookEventRetryCommand) => operatorRequest<WebhookEventRetryReceipt>(`${path}/${encodeURIComponent(id)}/retry`, { method: 'POST', body: command }),
      }
    }
    return {
      list: async (offset: number) => { const result = await (family === 'consent' ? azoa.api.listConsentWebhookEvents(offset, 25) : azoa.api.listWebhookEvents(offset, 25)); if (!result.ok) throw new Error(result.error.message); return result.value },
      get: async (id: string) => { const result = await (family === 'consent' ? azoa.api.getConsentWebhookEvent(id) : azoa.api.getWebhookEvent(id)); if (!result.ok) throw new Error(result.error.message); return result.value },
      retry: async (id: string, command: WebhookEventRetryCommand) => { const result = await (family === 'consent' ? azoa.api.retryConsentWebhookEvent(id, command) : azoa.api.retryWebhookEvent(id, command)); if (!result.ok) throw new Error(result.error.message); return result.value },
    }
  }, [operatorTenantId, family])
  const [rows, setRows] = useState<DeliveryEvent[]>([])
  const [offset, setOffset] = useState(0)
  const [saved, setSaved] = useState<SavedRetry | null>(null)
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const lock = useRef(false)
  const mounted = useRef(false)
  const readGeneration = useRef(0)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])
  const load = useCallback(async () => {
    const generation = ++readGeneration.current
    try {
      const value = await api.list(offset)
      if (generation === readGeneration.current) setRows(value)
    } catch (cause) {
      if (generation === readGeneration.current) setError(cause instanceof Error ? cause.message : 'Delivery history could not be read.')
    }
  }, [api, offset])
  useEffect(() => {
    try { const raw = localStorage.getItem(storageKey); if (raw) setSaved(parseSaved(raw)); setReady(true) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Saved retry could not be read.') }
  }, [storageKey])
  useEffect(() => {
    void load()
    return () => { ++readGeneration.current }
  }, [load])
  async function run(work: () => Promise<void>) {
    if (lock.current) return
    lock.current = true; setBusy(true); setError(null); setMessage(null)
    try { await work() } catch (cause) { if (mounted.current) setError(cause instanceof Error ? cause.message : 'Delivery operation failed.') }
    finally { lock.current = false; if (mounted.current) setBusy(false) }
  }
  function ownsSaved(pending: SavedRetry) {
    if (!mounted.current) return false
    const raw = localStorage.getItem(storageKey)
    if (!raw || JSON.stringify(parseSaved(raw)) !== JSON.stringify(pending))
      throw new Error('The saved retry changed. Refresh before making another change.')
    return true
  }
  async function submit(pending: SavedRetry) {
    const receipt = await api.retry(pending.eventId, pending.command)
    if (!ownsSaved(pending)) return
    if (receipt.requestId !== pending.command.requestId || receipt.eventId !== pending.eventId
      || receipt.version !== pending.command.expectedVersion + 1) throw new Error('Retry receipt did not match the saved command.')
    localStorage.removeItem(storageKey); setSaved(null)
    const from = receipt.retriedFrom === 'AttemptBudgetExhausted' ? ' (it had run out of attempts)'
      : receipt.retriedFrom === 'AbandonedAttempt' ? ' (its last attempt was abandoned mid-flight)' : ''
    setMessage(`Retry accepted${from}. Delivery keeps the original event identity and destination.`)
    await load()
  }
  async function start(row: DeliveryEvent) {
    if (localStorage.getItem(storageKey)) throw new Error('A saved retry must be recovered first. Refresh this panel.')
    if (!Number.isSafeInteger(row.version) || row.version < 0 || row.version >= Number.MAX_SAFE_INTEGER) throw new Error('The event version cannot be represented safely.')
    const pending = { eventId: row.eventId, command: { requestId: crypto.randomUUID(), expectedVersion: row.version, reason: reason.trim() } }
    localStorage.setItem(storageKey, JSON.stringify(pending)); setSaved(pending)
    await submit(pending)
  }
  async function reconcile() {
    if (!saved) return
    const observed = await api.get(saved.eventId)
    if (!ownsSaved(saved)) return
    if (observed.eventId !== saved.eventId || !Number.isSafeInteger(observed.version) || observed.version <= saved.command.expectedVersion)
      throw new Error('The event has not advanced beyond this command. Keep it and retry the exact request.')
    localStorage.setItem(`${storageKey}:archive:${saved.command.requestId}`, JSON.stringify({ saved, observed }))
    localStorage.removeItem(storageKey); setSaved(null)
    setMessage('Saved command archived against the newer event version. This does not cancel an accepted delivery.')
    await load()
  }
  return <section className="space-y-3 rounded-lg border p-4" aria-label={family === 'consent' ? 'Consent webhook deliveries' : 'Webhook deliveries'}>
    <h3 className="font-semibold">{family === 'consent' ? 'Consent webhook deliveries' : 'Webhook deliveries'}</h3>
    <p className="text-sm text-muted-foreground">Retry a delivery that stopped (dead-lettered, out of attempts, or abandoned mid-attempt) after fixing the receiver. Its original destination must still be active. Lifetime attempts remain recorded.</p>
    {error && <p role="alert">{error}</p>}
    {message && <p role="status">{message}</p>}
    {saved && <div className="space-y-2"><p>Saved retry for {saved.eventId}</p>
      <Button disabled={busy || !ready} onClick={() => void run(() => submit(saved))}>Recover saved retry</Button>
      <Button disabled={busy || !ready} variant="outline" onClick={() => void run(reconcile)}>Reconcile newer event</Button>
    </div>}
    <Input aria-label="Delivery retry reason" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} placeholder="Reason for retry (at least 10 characters)" />
    <ul className="space-y-3">{rows.map(row => <li key={row.eventId} className="rounded border p-3">
      <p>{row.eventType}: {row.status}</p><p className="break-all text-sm">{row.eventId}</p>
      <p className="text-sm">Attempts: {row.attemptCount}; this cycle: {row.cycleAttemptCount}; retries: {row.retryGeneration}</p>
      {row.errorCode && <p>{row.errorCode}</p>}
      {row.retryable && <Button disabled={busy || !ready || !!saved || row.attemptInFlight || reason.trim().length < 10}
        onClick={() => void run(() => start(row))}>Retry delivery</Button>}
    </li>)}</ul>
    <div className="flex gap-2">
      <Button variant="outline" disabled={busy || offset === 0} onClick={() => setOffset(Math.max(0, offset - 25))}>Previous deliveries</Button>
      <Button variant="outline" disabled={busy || rows.length < 25} onClick={() => setOffset(offset + 25)}>Next deliveries</Button>
      <Button variant="outline" disabled={busy} onClick={() => void run(load)}>Refresh deliveries</Button>
    </div>
  </section>
}
