'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import type { ActionRunResult, DeploymentArtifactView, DeploymentReceipt } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { DeploymentRecovery } from './deployment-recovery'

export function DeploymentOperations({ userId, selected, onSelect, onTerminal }: { userId: string; selected: DeploymentReceipt | null; onSelect: (value: DeploymentReceipt) => void; onTerminal: (value: DeploymentReceipt) => void }) {
  return <DeploymentOperationsSession key={`${userId}:${selected?.operationId ?? ''}`} userId={userId} selected={selected} onSelect={onSelect} onTerminal={onTerminal} />
}

function DeploymentOperationsSession({ userId, selected, onSelect, onTerminal }: { userId: string; selected: DeploymentReceipt | null; onSelect: (value: DeploymentReceipt) => void; onTerminal: (value: DeploymentReceipt) => void }) {
  const [items, setItems] = useState<DeploymentReceipt[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const alive = useRef(true)
  const selection = useRef({ userId, operationId: selected?.operationId })
  if (selection.current.userId !== userId || selection.current.operationId !== selected?.operationId)
    selection.current = { userId, operationId: selected?.operationId }
  const renderedSelection = selection.current
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const load = useCallback(async (after?: string) => {
    setBusy(true); setError(null)
    try {
      const result = await azoa.api.listDeployments(after)
      if (!alive.current) return
      if (!isOk(result)) { setError(result.error.message); return }
      setItems(previous => after ? [...previous.filter(item => !result.value.items.some(next => next.operationId === item.operationId)), ...result.value.items] : result.value.items)
      setCursor(result.value.nextCursor ?? null)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Discovery failed.') }
    finally { if (alive.current) setBusy(false) }
  }, [])
  useEffect(() => { void load() }, [load])
  const act = async (action: 'refresh' | 'continue' | 'cancel') => {
    if (!selected) return
    const expected = selection.current
    setBusy(true); setError(null)
    try {
      const result = await (action === 'refresh' ? azoa.api.getDeployment(selected.operationId) : action === 'continue' ? azoa.api.continueDeployment(selected.operationId) : azoa.api.cancelDeployment(selected.operationId))
      if (!alive.current || selection.current !== expected) return
      if (!isOk(result)) { setError(result.error.message); return }
      onSelect(result.value)
      if (['Confirmed', 'Failed', 'Cancelled', 'AbortedClean', 'FailedClean'].includes(result.value.state)) onTerminal(result.value)
    } catch (cause) { if (alive.current && selection.current === expected) setError(cause instanceof Error ? cause.message : 'Request failed.') }
    finally { if (alive.current && selection.current === expected) setBusy(false) }
  }
  return <div className="space-y-3 border-t pt-4">
    <h3 className="font-medium">Your deployment operations</h3>
    <p className="text-sm text-muted-foreground">Server records remain available after signing in on another device. Submitted transactions are reconciled automatically; later stages require your authorization.</p>
    <div className="flex gap-2"><Button variant="outline" disabled={busy} onClick={() => load()}>Reload operations</Button>{cursor && <Button variant="outline" disabled={busy} onClick={() => load(cursor)}>Load more</Button>}</div>
    {items.map(item => <Button key={item.operationId} variant="outline" className="block h-auto w-full whitespace-normal text-left" disabled={busy} onClick={() => onSelect(item)}>{item.target.chainType} / {item.target.network} · {item.state} · {item.stage} · {item.operationId}</Button>)}
    {selected && <div className="space-y-2 rounded border p-3">
      <p className="font-medium">{selected.state} · {selected.stage}</p><p className="break-all text-xs">Operation {selected.operationId}</p>
      {selected.state !== 'Confirmed' && <p>The full deployment workflow is not confirmed.</p>}
      {selected.resourceEffect && <p className="break-all">Confirmed resource effect: {selected.resourceEffect.kind} · {selected.resourceEffect.resourceId}</p>}
      {selected.cleanupEffect && <p className="break-all">Confirmed cleanup: {selected.cleanupEffect.kind} · {selected.cleanupEffect.resourceId}</p>}
      {selected.recovery?.childOperationId && <p className="break-all">Linked recovery operation: {selected.recovery.childOperationId}</p>}
      {selected.recovery?.replacedByOperationId && <p className="break-all">This recovery attempt was permanently replaced by {selected.recovery.replacedByOperationId}. Its receipts remain available.</p>}
      {selected.recovery?.replacesOperationId && <p className="break-all">Replaces recovery attempt: {selected.recovery.replacesOperationId}</p>}
      {selected.detail && <p>{selected.detail}</p>}
      {selected.resourceId && <p className="break-all">Resource: {selected.resourceId}</p>}
      {selected.transactionIds.map(id => <p key={id} className="break-all text-xs">Transaction: {id}</p>)}
      {selected.authorization && <div className="space-y-1 rounded border p-2 text-sm"><p>Original {selected.target.chainType} / {selected.target.network} authorization: {selected.authorization.kind}.</p><p>Whole-operation maximums in native base units: fee {selected.authorization.budget.maximumNetworkFee}; rent {selected.authorization.budget.maximumLockedRent}; value {selected.authorization.budget.maximumValue}.</p><p>Signing addresses: {selected.authorization.authorities.map(authority => authority.address).join(', ')}</p>{selected.authorization.quote.disclosure && <p>{selected.authorization.quote.disclosure.effect} {selected.authorization.quote.disclosure.refundDestination && `Return destination: ${selected.authorization.quote.disclosure.refundDestination}.`}</p>}<details><summary>Review exact authorized operation</summary><pre className="max-h-64 overflow-auto text-xs">{JSON.stringify({ input: selected.authorization.input, disclosure: selected.authorization.quote.disclosure }, null, 2)}</pre></details></div>}
      {selected.economics && <div className="space-y-1 text-sm"><p>Current reserved limits: fee {selected.economics.reservedNetworkFee}, rent {selected.economics.reservedRent}, value {selected.economics.reservedValue} native base units.</p><p>Observed network fees paid: {selected.economics.observedNetworkFeePaid}{selected.economics.networkFeeAccountingComplete ? '' : ' (accounting incomplete)'}. Reserved limits are not proof of a debit or refund.</p><details><summary>Confirmed native balance changes</summary><pre className="max-h-64 overflow-auto text-xs">{JSON.stringify(selected.economics.confirmedAccounting, null, 2)}</pre></details></div>}
      <div className="flex flex-wrap gap-2"><Button variant="outline" disabled={busy} onClick={() => act('refresh')}>Refresh receipt</Button>{!selected.recovery?.replacedByOperationId && !['Confirmed', 'Failed', 'Cancelled', 'AbortedClean', 'FailedClean', 'RecoveryQuoteRequired'].includes(selected.state) && <><Button disabled={busy} onClick={() => act('continue')}>Authorize / continue next stage</Button><Button variant="outline" disabled={busy} onClick={() => act('cancel')}>Cancel if no effect completed</Button></>}</div>
      {selected.authorization?.kind === 'Deploy' && ['DeployProgram', 'UpgradeProgram'].includes(selected.authorization.quote.disclosure?.effect ?? '')
        && !['Confirmed', 'Cancelled', 'AbortedClean', 'FailedClean'].includes(selected.state)
        && <DeploymentRecovery key={`${userId}:${selected.operationId}`} userId={userId} receipt={selected} onReceipt={value => { if (selection.current === renderedSelection) onSelect(value) }} />}
      {selected.state === 'Confirmed' && selected.target.kind === 'PlatformRuntime' && <RuntimeInvocation key={`${userId}:${selected.operationId}`} userId={userId} receipt={selected} />}
    </div>}
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </div>
}

function RuntimeInvocation({ userId, receipt }: { userId: string; receipt: DeploymentReceipt }) {
  const [artifact, setArtifact] = useState<DeploymentArtifactView | null>(null)
  const [input, setInput] = useState('{}')
  const [acknowledged, setAcknowledged] = useState(false)
  const [request, setRequest] = useState<{ key: string; input: unknown } | null>(null)
  const [run, setRun] = useState<ActionRunResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [ready, setReady] = useState(false)
  const alive = useRef(true)
  const storageKey = `azoa:deployment-invoke:${userId}:${receipt.operationId}`
  useEffect(() => {
    alive.current = true
    try { const raw = localStorage.getItem(storageKey); if (raw) { const saved = JSON.parse(raw) as { key: string; input: unknown }; if (!saved.key) throw new Error('Invalid request'); setRequest(saved); setInput(JSON.stringify(saved.input, null, 2)); setAcknowledged(true) }; setReady(true) }
    catch { setError('Saved invocation cannot be read. Preserve it until the original request is reconciled.') }
    void azoa.api.getDeploymentArtifact(receipt.artifactId).then(result => { if (!alive.current) return; if (isOk(result)) setArtifact(result.value); else setError(result.error.message) }).catch(cause => { if (alive.current) setError(String(cause)) })
    return () => { alive.current = false }
  }, [storageKey, receipt.artifactId])
  const invoke = async () => {
    if (!artifact || !acknowledged) return
    setBusy(true); setError(null)
    try {
      const saved = request ?? { key: crypto.randomUUID(), input: JSON.parse(input) as unknown }
      localStorage.setItem(storageKey, JSON.stringify(saved)); setRequest(saved)
      const result = await azoa.api.invokeDeployment(receipt.operationId, { expectedPayloadHash: artifact.payloadHash, input: saved.input, acknowledgeEconomicEffects: true }, saved.key)
      if (!alive.current) return
      if (!isOk(result)) { setError(result.error.message); return }
      setRun(result.value)
    } catch (cause) { if (alive.current) setError(cause instanceof Error ? cause.message : 'Invocation failed.') }
    finally { if (alive.current) setBusy(false) }
  }
  const refresh = async () => {
    if (!run) return
    setBusy(true)
    try { const result = await azoa.api.getActionRun(run.id); if (alive.current) { if (isOk(result)) setRun(result.value); else setError(result.error.message) } }
    finally { if (alive.current) setBusy(false) }
  }
  return <div className="space-y-2 border-t pt-3"><h4 className="font-medium">Invoke registered workflow</h4>
    <p className="text-sm">The workflow runs as you and requires your current permissions and consent. It may move assets or charge network fees.</p>
    <Textarea aria-label="Workflow input" value={input} disabled={!!request || busy} onChange={event => setInput(event.target.value)} />
    <label className="flex gap-2"><input type="checkbox" checked={acknowledged} disabled={!!request || busy} onChange={event => setAcknowledged(event.target.checked)} />I authorize this workflow&apos;s economic effects under my current permissions.</label>
    <Button disabled={busy || !ready || !artifact || !acknowledged} onClick={invoke}>{request ? 'Recover original invocation' : 'Invoke workflow'}</Button>
    {artifact?.loader && <Button variant="outline" onClick={() => { const url = URL.createObjectURL(new Blob([artifact.loader!], { type: 'text/javascript' })); const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'azoa-deployment.mjs'; anchor.click(); URL.revokeObjectURL(url) }}>Download executable loader</Button>}
    {run && <><p>Run {run.id}: {run.status}. Admission is not execution success.</p><Button variant="outline" disabled={busy} onClick={refresh}>Refresh run</Button>{['Succeeded', 'Failed', 'Cancelled', 'Forked'].includes(run.status) && <Button variant="outline" disabled={busy} onClick={() => { localStorage.removeItem(storageKey); setRequest(null); setRun(null); setAcknowledged(false) }}>Prepare a new invocation</Button>}</>}
    {error && <p role="alert" className="text-destructive">{error}</p>}
  </div>
}
