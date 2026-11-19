'use client'

import { useCallback, useEffect, useState } from 'react'
import type { ActionRecoveryAudit, ActionRecoveryReceipt, ActionRecoveryRequest, ActionRecoveryView } from 'azoa-sdk'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { operatorRequest } from '@/lib/operator-client'

interface PublishPolicyView {
  requireOperatorApprovalForPublish: boolean
  updatedAt: string
  updatedBy?: string | null
}

interface PublishApprovalItem {
  actionId: string
  name: string
  description?: string | null
  userId: string
  isPublic: boolean
  status: string
  approvalStatus: string
  versionHash?: string | null
  nodeCount: number
  edgeCount: number
  requestedAt?: string | null
  requestedBy?: string | null
  decidedAt?: string | null
  decidedBy?: string | null
  reason?: string | null
}

export default function ActionRecoveryPage() {
  const [rows, setRows] = useState<ActionRecoveryView[]>([])
  const [selected, setSelected] = useState<ActionRecoveryView | null>(null)
  const [history, setHistory] = useState<ActionRecoveryAudit[]>([])
  const [reason, setReason] = useState('')
  const [request, setRequest] = useState<ActionRecoveryRequest | null>(null)
  const [policy, setPolicy] = useState<PublishPolicyView | null>(null)
  const [approvals, setApprovals] = useState<PublishApprovalItem[]>([])
  const [approvalReasons, setApprovalReasons] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const load = useCallback(async () => {
    try {
      const [recoveryRows, policyView, approvalRows] = await Promise.all([
        operatorRequest<ActionRecoveryView[]>('action-recovery?limit=100'),
        operatorRequest<PublishPolicyView>('action-recovery/publish-policy'),
        operatorRequest<PublishApprovalItem[]>('action-recovery/publish-approvals?limit=50'),
      ])
      setRows(recoveryRows)
      setPolicy(policyView)
      setApprovals(approvalRows)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Action recovery and publish approvals could not be loaded.')
    }
  }, [])
  useEffect(() => { void load() }, [load])

  async function togglePublishPolicy() {
    if (!policy) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const next = await operatorRequest<PublishPolicyView>('action-recovery/publish-policy', {
        method: 'PUT',
        body: { requireOperatorApprovalForPublish: !policy.requireOperatorApprovalForPublish },
      })
      setPolicy(next)
      setNotice(`Workflow publish approval gate is now ${next.requireOperatorApprovalForPublish ? 'enabled' : 'disabled'}.`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Publish approval policy could not be updated.')
    } finally {
      setBusy(false)
    }
  }

  async function decidePublish(item: PublishApprovalItem, approve: boolean) {
    const decisionReason = (approvalReasons[item.actionId] ?? '').trim()
    if (!approve && !decisionReason) {
      setError('Enter a reason before rejecting a workflow publish request.')
      return
    }
    setBusy(true); setError(null); setNotice(null)
    try {
      const result = await operatorRequest<PublishApprovalItem>(`action-recovery/publish-approvals/${item.actionId}/decision`, {
        method: 'POST',
        body: decisionReason ? { approve, reason: decisionReason } : { approve },
      })
      setNotice(`${result.name}: ${result.approvalStatus} (${result.status}).`)
      await load()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Workflow publish decision could not be recorded.')
    } finally {
      setBusy(false)
    }
  }

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

  const pendingApprovals = approvals.filter(item => item.approvalStatus === 'PendingApproval')

  return <main className="space-y-6">
    <h1 className="text-2xl font-semibold">Actions &amp; Publish Approvals</h1>
    {error && <p role="alert" className="text-destructive">{error}</p>}
    {notice && <p role="status">{notice}</p>}

    <section className="space-y-4 rounded border border-border p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="space-y-1">
          <div className="flex items-center gap-2">
            <h2 className="text-lg font-semibold">Workflow publish approval gate</h2>
            {policy && <Badge variant={policy.requireOperatorApprovalForPublish ? 'default' : 'outline'}>
              {policy.requireOperatorApprovalForPublish ? 'Required' : 'Optional / Off'}
            </Badge>}
          </div>
          <p className="text-sm text-muted-foreground">
            When enabled, validated workflows stay in Draft with PendingApproval status until an operator approves activation. Startup default is controlled by ActionDefinition:RequireOperatorApprovalForPublish.
          </p>
        </div>
        {policy && <Button variant="outline" disabled={busy} onClick={() => void togglePublishPolicy()}>
          {policy.requireOperatorApprovalForPublish ? 'Disable approval requirement' : 'Require operator approval'}
        </Button>}
      </div>

      <div className="space-y-3 border-t pt-3">
        <div className="flex items-center justify-between">
          <h3 className="font-medium">Workflow publish review queue ({pendingApprovals.length} pending)</h3>
          <Button size="sm" variant="outline" disabled={busy} onClick={() => { setError(null); void load() }}>Refresh</Button>
        </div>
        {approvals.length === 0 ? <p className="text-sm text-muted-foreground">No workflow publish requests recorded.</p> : approvals.map(item => (
          <div key={item.actionId} className="space-y-2 rounded border p-3 text-sm">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div>
                <p className="font-medium">{item.name}</p>
                {item.description && <p className="text-xs text-muted-foreground">{item.description}</p>}
              </div>
              <div className="flex items-center gap-1.5">
                <Badge variant="outline">{item.approvalStatus}</Badge>
                <Badge variant="outline">{item.status}</Badge>
                <Badge variant="outline">{item.nodeCount} nodes</Badge>
              </div>
            </div>
            <p className="break-all text-xs text-muted-foreground">Action {item.actionId} · Author {item.userId}{item.versionHash ? ` · Hash ${item.versionHash.slice(0, 12)}` : ''}</p>
            {item.reason && <p className="text-xs">Decision reason: {item.reason}</p>}
            {item.approvalStatus === 'PendingApproval' && (
              <div className="flex flex-wrap items-center gap-2 pt-1">
                <Input
                  aria-label={`Decision reason for ${item.name}`}
                  className="max-w-sm"
                  placeholder="Approval note or rejection reason (required on reject)"
                  value={approvalReasons[item.actionId] ?? ''}
                  disabled={busy}
                  onChange={event => setApprovalReasons(current => ({ ...current, [item.actionId]: event.target.value }))}
                />
                <Button size="sm" disabled={busy} onClick={() => void decidePublish(item, true)}>Approve &amp; Activate</Button>
                <Button size="sm" variant="destructive" disabled={busy} onClick={() => void decidePublish(item, false)}>Reject</Button>
              </div>
            )}
          </div>
        ))}
      </div>
    </section>

    <section className="space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold">Action execution recovery</h2>
        <Button variant="outline" disabled={busy} onClick={() => { setError(null); void load() }}>Refresh parked actions</Button>
      </div>
      <p className="text-sm text-muted-foreground">Inspect parked workflow operations after resolving their underlying provider or authorization issue. A retry resumes the original saved operation under its current authority checks.</p>
      {rows.length === 0 && <p>No parked actions need operator review.</p>}
      {rows.map(row => <section key={row.executionId} className="border border-border p-4 space-y-2">
        <p className="font-medium">{row.domain ?? 'Uncorrelated legacy operation'} · {row.state}</p>
        <p className="break-all text-sm">Run {row.runId} · Node {row.nodeId}</p>
        <Button variant="outline" disabled={busy} onClick={() => void inspect(row)}>Inspect saved operation</Button>
      </section>)}
      {selected && <section className="border border-border p-4 space-y-3">
        <h3 className="font-semibold">Saved recovery identity</h3>
        <p className="break-all text-sm">Operation {selected.operationId ?? 'Not yet attached'} · Version {selected.version}</p>
        <p className="break-all text-sm">Intent {selected.intentHash ?? 'No immutable input available'}</p>
        {!selected.canRetry ? <p>This operation lacks a supported immutable recovery identity. Its effects remain quarantined. Use the original domain recovery evidence to investigate it.</p>
          : <form className="space-y-3" onSubmit={event => { event.preventDefault(); void retry() }}>
            <Label htmlFor="action-recovery-reason">Investigation and reason for retry</Label>
            <Input id="action-recovery-reason" value={request?.reason ?? reason} disabled={busy || request !== null} minLength={20} maxLength={1000} required onChange={event => setReason(event.target.value)} />
            <Button type="submit" disabled={busy || (request?.reason ?? reason).trim().length < 20}>{request ? 'Retry same recovery request' : 'Request recovery'}</Button>
          </form>}
        <h4 className="font-medium">Recent recovery requests</h4>
        {history.length === 0 ? <p>No recovery requests recorded.</p> : history.map(item => <div key={item.id} className="border-t py-2">
          <p className="text-sm">{item.createdAt} · Version {item.expectedVersion}</p><p>{item.reason}</p>
        </div>)}
      </section>}
    </section>
  </main>
}
