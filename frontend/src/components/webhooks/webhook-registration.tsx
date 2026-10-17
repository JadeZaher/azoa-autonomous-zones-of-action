"use client"

import { useEffect, useMemo, useRef, useState } from 'react'
import type { WebhookRegistrationCommand, WebhookRegistrationStatus, WebhookRegistrationMutationResult, WebhookRegistrationWithdrawal } from 'azoa-sdk'
import { operatorRequest } from '@/lib/operator-client'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

export function WebhookRegistrationPanel({ userId }: { userId: string }) {
  return <RegistrationEditor key={userId} userId={userId} />
}

export function OperatorWebhookRegistrationPanel({ tenantId }: { tenantId: string }) {
  return <RegistrationEditor key={tenantId} userId="node-operator" operatorTenantId={tenantId} />
}

function RegistrationEditor({ userId, operatorTenantId }: { userId: string; operatorTenantId?: string }) {
  const storageKey = operatorTenantId ? `azoa:operator-webhook-registration:${operatorTenantId}` : `azoa:webhook-registration:${userId}`
  const api = useMemo(() => {
    if (!operatorTenantId) return {
      get: () => azoa.api.getWebhookRegistration(),
      update: (command: WebhookRegistrationCommand) => azoa.api.updateWebhookRegistration(command),
      withdraw: (command: WebhookRegistrationCommand) => azoa.api.withdrawWebhookRegistration(command),
    }
    const path = `tenants/${encodeURIComponent(operatorTenantId)}/webhook-registration`
    return {
      get: async () => ({ ok: true as const, value: await operatorRequest<WebhookRegistrationStatus | null>(path) }),
      update: async (command: WebhookRegistrationCommand) => ({ ok: true as const,
        value: await operatorRequest<WebhookRegistrationMutationResult>(path, { method: 'PUT', body: command }) }),
      withdraw: async (command: WebhookRegistrationCommand) => ({ ok: true as const,
        value: await operatorRequest<WebhookRegistrationWithdrawal>(`${path}/withdraw`, { method: 'POST', body: command }) }),
    }
  }, [operatorTenantId])
  const [status, setStatus] = useState<WebhookRegistrationStatus | null>(null)
  const [saved, setSaved] = useState<WebhookRegistrationCommand | null>(null)
  const [url, setUrl] = useState('')
  const [active, setActive] = useState(true)
  const [rotate, setRotate] = useState(false)
  const [reason, setReason] = useState('')
  const [legacyConsent, setLegacyConsent] = useState(false)
  const [secret, setSecret] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const [busy, setBusy] = useState(false)
  const alive = useRef(false)
  const pending = useRef(false)

  useEffect(() => {
    let canceled = false
    alive.current = true
    const load = async () => {
      try {
        const stored = localStorage.getItem(storageKey)
        if (stored) {
          const command = JSON.parse(stored) as WebhookRegistrationCommand
          if (!command || typeof command.requestId !== 'string' || typeof command.registrationId !== 'string'
            || !Number.isSafeInteger(command.expectedVersion) || command.expectedVersion < 0
            || typeof command.url !== 'string' || typeof command.reason !== 'string'
            || typeof command.isActive !== 'boolean' || typeof command.rotateSecret !== 'boolean') throw new Error('Saved command is unreadable. Preserve it before starting another change.')
          if (!canceled) setSaved(command)
        }
        const result = await api.get()
        if (canceled) return
        if (!isOk(result)) throw new Error(result.error.message)
        setStatus(result.value); setUrl(result.value?.url ?? ''); setActive(result.value?.isActive ?? true); setReady(true)
      } catch (cause) { if (!canceled) setError(cause instanceof Error ? cause.message : 'Registration could not be loaded.') }
    }
    void load()
    return () => { canceled = true; alive.current = false }
  }, [storageKey, api])

  const submit = async () => {
    if (pending.current || !ready) return
    pending.current = true; setBusy(true); setError(null); setMessage(null); setSecret(null)
    try {
      let command = saved
      if (!command) {
        if (active || !status || url !== status.url) {
          const target = new URL(url)
          if (target.protocol !== 'https:' || target.username || target.password || target.hash) throw new Error('Use an HTTPS destination without credentials or a fragment.')
        }
        if (reason.trim().length < 10 || reason.trim().length > 1000) throw new Error('Enter a reason between 10 and 1000 characters.')
        if (status?.legacyFingerprint && !legacyConsent) throw new Error('Confirm replacement of the legacy signing secret.')
        command = { requestId: crypto.randomUUID(), registrationId: status?.registrationId ?? crypto.randomUUID(),
          expectedVersion: status?.version ?? 0, url, isActive: active, rotateSecret: rotate, reason: reason.trim(),
          replaceLegacy: Boolean(status?.legacyFingerprint), legacyFingerprint: status?.legacyFingerprint ?? null }
        localStorage.setItem(storageKey, JSON.stringify(command))
        setSaved(command)
      }
      const result = await api.update(command)
      if (!alive.current) return
      if (!isOk(result)) throw new Error(result.error.message)
      setSecret(result.value.newSecret ?? null)
      setMessage(result.value.replayed
        ? 'The original change was accepted. Its secret cannot be shown again. Rotate explicitly if the original response was lost.'
        : 'Registration saved. Copy the new secret now if one is shown; it is not stored in this browser.')
      const receipt = result.value.receipt
      setStatus({ ...receipt, createdAt: status?.createdAt ?? receipt.recordedAt, legacyFingerprint: null })
      setUrl(receipt.url); setActive(receipt.isActive); setRotate(false); setLegacyConsent(false)
      localStorage.removeItem(storageKey); setSaved(null)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Response unavailable. Retry the saved command.') }
    finally { pending.current = false; if (alive.current) setBusy(false) }
  }

  const reconcile = async () => {
    if (pending.current || !saved) return
    pending.current = true; setBusy(true); setError(null)
    try {
      const result = await api.get()
      if (!alive.current) return
      if (!isOk(result)) throw new Error(result.error.message)
      if (!result.value || result.value.version <= saved.expectedVersion) {
        setMessage('The saved change is still unresolved. Retry it before creating another command.'); return
      }
      setSecret(null); setStatus(result.value); setUrl(result.value.url); setActive(result.value.isActive)
      localStorage.removeItem(storageKey); setSaved(null)
      setMessage('A newer registration version prevents the saved command from applying again. Review the current state before another change.')
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Reconciliation failed.') }
    finally { pending.current = false; if (alive.current) setBusy(false) }
  }

  const withdraw = async () => {
    if (pending.current || !saved) return
    pending.current = true; setBusy(true); setError(null); setSecret(null)
    try {
      const result = await api.withdraw(saved)
      if (!alive.current) return
      if (!isOk(result)) throw new Error(result.error.message)
      const current = await api.get()
      if (!alive.current) return
      if (!isOk(current)) throw new Error(current.error.message)
      setStatus(current.value); setUrl(current.value?.url ?? ''); setActive(current.value?.isActive ?? true)
      setRotate(false); setLegacyConsent(false)
      localStorage.removeItem(storageKey); setSaved(null)
      setMessage(result.value.alreadyApplied
        ? 'The original change had already applied. Its secret cannot be redisclosed; rotate explicitly if needed.'
        : 'The saved change was withdrawn. It cannot apply later. You can now edit a new command.')
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Withdrawal response unavailable. Keep the saved command and retry withdrawal.') }
    finally { pending.current = false; if (alive.current) setBusy(false) }
  }

  return <section className="space-y-3 rounded border p-4" aria-label="Webhook registration">
    <h2 className="font-semibold">Webhook registration</h2>
    <p className="text-sm">Configure your tenant receiver. Disabling or rotating cannot recall an already authorized delivery.</p>
    {status && <p className="text-sm">Version {status.version}; destination {status.destinationRevision}; signing secret {status.secretRevision}.</p>}
    <label className="block">HTTPS destination<Input aria-label="HTTPS destination" value={url} onChange={event => setUrl(event.target.value)} disabled={!ready || busy || Boolean(saved)} maxLength={2048} /></label>
    <label className="flex gap-2"><input type="checkbox" checked={active} onChange={event => setActive(event.target.checked)} disabled={!ready || busy || Boolean(saved)} />Enable delivery</label>
    <label className="flex gap-2"><input type="checkbox" checked={rotate} onChange={event => setRotate(event.target.checked)} disabled={!ready || busy || Boolean(saved)} />Generate a new signing secret</label>
    {status?.legacyFingerprint && <label className="flex gap-2"><input type="checkbox" checked={legacyConsent} onChange={event => setLegacyConsent(event.target.checked)} disabled={busy || Boolean(saved)} />Replace the legacy secret with a newly generated secret</label>}
    <label className="block">Reason<Input aria-label="Registration change reason" value={reason} onChange={event => setReason(event.target.value)} disabled={!ready || busy || Boolean(saved)} maxLength={1000} /></label>
    {saved && <p className="text-sm">A saved change is awaiting confirmation. Retrying preserves its request ID, expected version, destination and rotation choice.</p>}
    <div className="flex gap-2"><Button onClick={() => void submit()} disabled={!ready || busy}>{saved ? 'Retry saved change' : 'Save registration'}</Button>
      {saved && <><Button variant="outline" onClick={() => void reconcile()} disabled={busy}>Check current version</Button><Button variant="outline" onClick={() => void withdraw()} disabled={busy}>Withdraw saved change</Button></>}</div>
    {secret && <div><label>New signing secret<Input aria-label="New signing secret" readOnly value={secret} autoComplete="off" /></label><Button variant="outline" onClick={() => setSecret(null)}>Hide secret</Button></div>}
    {message && <p role="status">{message}</p>}{error && <p role="alert">{error}</p>}
  </section>
}
