'use client'

import { useCallback, useEffect, useState } from 'react'
import type { ActionRecoveryAudit, ActionRecoveryReceipt, ActionRecoveryRequest, ActionRecoveryView } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { operatorRequest } from '@/lib/operator-client'

export default function ActionRecoveryPage() {
  const [rows, setRows] = useState<ActionRecoveryView[]>([])
  const [selected, setSelected] = useState<ActionRecoveryView | null>(null)
  const [history, setHistory] = useState<ActionRecoveryAudit[]>([])
  const [reason, setReason] = useState('')
  const [request, setRequest] = useState<ActionRecoveryRequest | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const load = useCallback(async () => {
    try { setRows(await operatorRequest<ActionRecoveryView[]>('action-recovery?limit=100')) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Action recovery could not be loaded.') }
  }, [])
  useEffect(() => { void load() }, [load])

  async function inspect(row: ActionRecoveryView) {
    setBusy(true); setError(null); setRequest(null); setReason(''); setHistory([]); setSelected(null)
    try {
      const current = await operatorRequest<ActionRecoveryView>(`action-recovery/${row.executionId}`)
      const audit = await operatorRequest<ActionRecoveryAudit[]>(`action-recovery/${row.executionId}/history`)
      setSelected(current); setHistory(audit)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'The action could not be inspected.') }
    finally { setBusy(false) }
  }

  async function retry() {
    if (!selected?.canRetry || !selected.intentHash) return
    const body = request ?? { requestId: crypto.randomUUID(), expectedVersion: selected.version, intentHash: selected.intentHash, reason: reason.trim() }
    setRequest(body); setBusy(true); setError(null); setNotice(null)
    try {
      const receipt = await operatorRequest<ActionRecoveryReceipt>(`action-recovery/${selected.executionId}/retry`, { method: 'POST', body })
      setNotice(`Recovery requested. Audit reference: ${receipt.auditId}. The workflow will check its original operation.`)
      setSelected(null); setRequest(null); await load()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Recovery was not acknowledged. Retry uses the same request ID.') }
    finally { setBusy(false) }
  }

  return <main className="space-y-6">
    <h1 className="text-2xl font-semibold">Action recovery</h1>
    <p>Inspect parked workflow operations after resolving their underlying provider or authorization issue. A retry resumes the original saved operation under its current authority checks. It can finish an admitted local change or reconcile an authorized transaction.</p>
    <Button variant="outline" disabled={busy} onClick={() => { setError(null); void load() }}>Refresh parked actions</Button>
    {error && <p role="alert" className="text-destructive">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    {rows.length === 0 && <p>No parked actions need operator review.</p>}
    {rows.map(row => <section key={row.executionId} className="border border-border p-4 space-y-2">
      <p className="font-medium">{row.domain ?? 'Uncorrelated legacy operation'} · {row.state}</p>
      <p className="break-all text-sm">Run {row.runId} · Node {row.nodeId}</p>
      <Button variant="outline" disabled={busy} onClick={() => void inspect(row)}>Inspect saved operation</Button>
    </section>)}
    {selected && <section className="border border-border p-4 space-y-3">
      <h2 className="font-semibold">Saved recovery identity</h2>
      <p className="break-all text-sm">Operation {selected.operationId ?? 'Not yet attached'} · Version {selected.version}</p>
      <p className="break-all text-sm">Intent {selected.intentHash ?? 'No immutable input available'}</p>
      {!selected.canRetry ? <p>This operation lacks a supported immutable recovery identity. Its effects remain quarantined. Use the original domain recovery evidence to investigate it.</p>
        : <form className="space-y-3" onSubmit={event => { event.preventDefault(); void retry() }}>
          <Label htmlFor="action-recovery-reason">Investigation and reason for retry</Label>
          <Input id="action-recovery-reason" value={request?.reason ?? reason} disabled={busy || request !== null} minLength={20} maxLength={1000} required onChange={event => setReason(event.target.value)} />
          <Button type="submit" disabled={busy || (request?.reason ?? reason).trim().length < 20}>{request ? 'Retry same recovery request' : 'Request recovery'}</Button>
        </form>}
      <h3 className="font-medium">Recent recovery requests</h3>
      {history.length === 0 ? <p>No recovery requests recorded.</p> : history.map(item => <div key={item.id} className="border-t py-2">
        <p className="text-sm">{item.createdAt} · Version {item.expectedVersion}</p><p>{item.reason}</p>
      </div>)}
    </section>}
  </main>
}
