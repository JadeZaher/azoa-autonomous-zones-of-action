'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import type { ConsentGrantCommand, ConsentGrantView, ConsentGrantReceipt } from 'azoa-sdk'
import { azoa } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const emptyId = '00000000-0000-0000-0000-000000000000'

function readCommand(raw: string): ConsentGrantCommand {
  const value = JSON.parse(raw) as ConsentGrantCommand
  if (!value || !uuid.test(value.requestId) || value.requestId === emptyId
    || !uuid.test(value.tenantId) || value.tenantId === emptyId || value.origin !== 'UserExplicit'
    || value.participationRef != null || !Array.isArray(value.scopes) || !value.scopes.length
    || value.scopes.some(scope => typeof scope !== 'string' || !/^[a-z]+(?::[a-z]+)+$/.test(scope))
    || (value.expiresAt != null && !Number.isFinite(Date.parse(value.expiresAt))))
    throw new Error('The saved consent request is invalid. Preserve it for support.')
  return value
}

export function ConsentGrantsPanel({ userId }: { userId: string }) {
  return <GrantEditor key={userId} userId={userId} />
}

function GrantEditor({ userId }: { userId: string }) {
  const storageKey = `azoa:consent-grant:${userId}`
  const [rows, setRows] = useState<ConsentGrantView[]>([])
  const [pending, setPending] = useState<ConsentGrantCommand | null>(null)
  const [tenantId, setTenantId] = useState('')
  const [scopes, setScopes] = useState('')
  const [expiresAt, setExpiresAt] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const active = useRef(false)
  const lock = useRef(false)
  const generation = useRef(0)

  const load = useCallback(async () => {
    const current = ++generation.current
    const result = await azoa.api.listConsentGrants()
    if (!active.current || current !== generation.current) return
    if (!result.ok) throw new Error(result.error.message)
    setRows(result.value)
  }, [])

  useEffect(() => {
    active.current = true
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) setPending(readCommand(raw))
      setReady(true)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Saved consent could not be read.') }
    void load().catch(cause => { if (active.current) setError(cause instanceof Error ? cause.message : 'Consent could not be read.') })
    return () => { active.current = false; ++generation.current }
  }, [storageKey, load])

  async function run(work: () => Promise<void>) {
    if (lock.current) return
    lock.current = true; setBusy(true); setError(null); setMessage(null)
    try { await work() }
    catch (cause) { if (active.current) setError(cause instanceof Error ? cause.message : 'Consent operation failed.') }
    finally { lock.current = false; if (active.current) setBusy(false) }
  }

  async function submit(command: ConsentGrantCommand) {
    const result = await azoa.api.grantConsent(command)
    if (!active.current) return
    if (!result.ok) throw new Error(result.error.message)
    await complete(command, result.value)
  }

  function ownsSaved(command: ConsentGrantCommand) {
    if (!active.current) return false
    const stored = localStorage.getItem(storageKey)
    if (!stored || JSON.stringify(readCommand(stored)) !== JSON.stringify(command))
      throw new Error('The saved consent request changed. Refresh before continuing.')
    return true
  }

  async function complete(command: ConsentGrantCommand, receipt: ConsentGrantReceipt) {
    if (receipt.requestId !== command.requestId || receipt.grantorUserId !== userId
      || receipt.tenantId !== command.tenantId || receipt.origin !== command.origin
      || receipt.participationRef != null || !uuid.test(receipt.grantId)
      || [...receipt.scopes].sort().join(',') !== [...command.scopes].sort().join(',')
      || (receipt.expiresAt ? Date.parse(receipt.expiresAt) : null) !== (command.expiresAt ? Date.parse(command.expiresAt) : null))
      throw new Error('Consent receipt did not match the saved request.')
    if (!ownsSaved(command)) return
    localStorage.setItem(`${storageKey}:receipt:${command.requestId}`, JSON.stringify(receipt))
    localStorage.removeItem(storageKey); setPending(null)
    setMessage('The original grant receipt was recovered. The list shows its current status.')
    await load()
  }

  async function withdraw(command: ConsentGrantCommand) {
    const result = await azoa.api.withdrawConsentCommand(command)
    if (!active.current) return
    if (!result.ok) throw new Error(result.error.message)
    if (result.value.requestId !== command.requestId) throw new Error('Withdrawal did not match the saved request.')
    if (!result.value.withdrawn) {
      if (!result.value.receipt) throw new Error('The admitted grant receipt is missing.')
      await complete(command, result.value.receipt)
      if (active.current) setMessage('The request already created a grant. Review its current status below and revoke it if needed.')
      return
    }
    if (result.value.receipt !== null) throw new Error('Withdrawal returned conflicting admission evidence.')
    if (!ownsSaved(command)) return
    localStorage.setItem(`${storageKey}:withdrawn:${command.requestId}`, JSON.stringify({ command, result: result.value }))
    localStorage.removeItem(storageKey); setPending(null)
    setMessage('The saved request was withdrawn. Delayed submissions of it cannot create a grant.')
    await load()
  }

  async function grant() {
    if (localStorage.getItem(storageKey)) throw new Error('Recover the saved consent request first.')
    const command = readCommand(JSON.stringify({ requestId: crypto.randomUUID(), tenantId: tenantId.trim().toLowerCase(),
      scopes: [...new Set(scopes.split(/[\s,]+/).filter(Boolean))].sort(), origin: 'UserExplicit',
      expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null }))
    if (command.expiresAt && Date.parse(command.expiresAt) <= Date.now()) throw new Error('Choose an expiry in the future.')
    localStorage.setItem(storageKey, JSON.stringify(command)); setPending(command)
    await submit(command)
  }

  async function revoke(grantId: string) {
    const result = await azoa.api.revokeConsent(grantId)
    if (!active.current) return
    if (!result.ok) throw new Error(result.error.message)
    if (result.value.grantId !== grantId) throw new Error('Revocation receipt did not match this grant.')
    setMessage('Consent revoked. Repeating this request returns the original revocation.')
    await load()
  }

  return <section className="space-y-3 rounded-lg border p-4" aria-label="Tenant permissions">
    <h3 className="font-semibold">Tenant permissions</h3>
    <p className="text-sm text-muted-foreground">Review or revoke the access you granted to a tenant. Grant only the scopes you intend to authorize.</p>
    {error && <p role="alert">{error}</p>}
    {message && <p role="status">{message}</p>}
    {pending && <div className="space-y-2"><p>Saved request for {pending.tenantId}: {pending.scopes.join(', ')}</p>
      <Button disabled={busy || !ready} onClick={() => void run(() => submit(pending))}>Recover saved consent</Button>
      <Button variant="outline" disabled={busy || !ready} onClick={() => void run(() => withdraw(pending))}>Withdraw saved request</Button>
    </div>}
    <Input aria-label="Tenant ID for consent" value={tenantId} onChange={event => setTenantId(event.target.value)} />
    <Input aria-label="Consent scopes" value={scopes} onChange={event => setScopes(event.target.value)} placeholder="For example: kyc:read" />
    <label className="block text-sm">Expires at (optional, local time)
      <Input type="datetime-local" aria-label="Consent expiry" value={expiresAt} onChange={event => setExpiresAt(event.target.value)} />
    </label>
    <Button disabled={busy || !ready || !!pending || !tenantId.trim() || !scopes.trim()} onClick={() => void run(grant)}>Grant selected permissions</Button>
    <ul className="space-y-3">{rows.map(row => <li key={row.grantId} className="rounded border p-3">
      <p className="break-all">Tenant: {row.tenantId}</p><p>{row.scopes.join(', ')}</p>
      <p>{row.revokedAt ? `Revoked ${row.revokedAt}` : row.expiresAt ? `Expires ${row.expiresAt}` : 'No expiry'}</p>
      {!row.revokedAt && <Button variant="destructive" disabled={busy} onClick={() => void run(() => revoke(row.grantId))}>Revoke permissions</Button>}
    </li>)}</ul>
    <Button variant="outline" disabled={busy} onClick={() => void run(load)}>Refresh permissions</Button>
  </section>
}
