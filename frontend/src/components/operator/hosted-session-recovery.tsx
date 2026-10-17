'use client'

import { useCallback, useEffect, useState } from 'react'
import type { HostedSessionRecoveryView } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { operatorRequest } from '@/lib/operator-client'
import { FlatCard, OperatorError, StatusPill, formatOperatorDate } from '@/components/operator/operator-ui'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'

export function HostedSessionRecovery() {
  const [rows, setRows] = useState<HostedSessionRecoveryView[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [selected, setSelected] = useState<HostedSessionRecoveryView | null>(null)
  const [providerId, setProviderId] = useState('')
  const [redirect, setRedirect] = useState('')
  const [reason, setReason] = useState('')
  const [reviewed, setReviewed] = useState(false)
  const [absent, setAbsent] = useState(false)
  const load = useCallback(async () => {
    try { setRows(await operatorRequest<HostedSessionRecoveryView[]>('hosted-sessions/reconciliation?limit=50')) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Hosted recovery could not be loaded.') }
  }, [])
  useEffect(() => { void load() }, [load])

  async function recover(row: HostedSessionRecoveryView, action: 'refresh' | 'reconcile') {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const receipt = await operatorRequest<{ status?: string }>(`hosted-sessions/${row.sessionId}/${action}`, { method: 'POST', body: action === 'refresh' ? {} : {
        reason: reason.trim(), operatorReviewedSubjectAndProvider: reviewed, confirmedProviderAbsence: absent,
        providerSessionId: absent ? undefined : providerId.trim(), redirectUrl: absent ? undefined : redirect.trim() || undefined,
      } })
      setNotice(action === 'refresh'
        ? `Provider re-read: the attempt is now ${receipt?.status ?? 'unchanged'}.`
        : 'Recovery recorded against the saved attempt.')
      setSelected(null)
      await load()
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Provider recovery remains unresolved.') }
    finally { setBusy(false) }
  }

  function select(row: HostedSessionRecoveryView) {
    setSelected(row); setProviderId(''); setRedirect(''); setReason(''); setReviewed(false); setAbsent(false)
  }

  return <FlatCard>
    <CardHeader><CardTitle>Hosted verification recovery</CardTitle></CardHeader>
    <CardContent className="space-y-4">
      <p className="text-sm text-muted-foreground">Attempts with no confirmed provider session attached, and attached sessions with no provider outcome for an hour (Awaiting provider outcome). Refresh re-reads the provider for the original request. A provider reference is verified against the saved attempt; this action cannot approve an identity.</p>
      {error && <OperatorError message={error} onRetry={() => void load()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {rows.length === 0 && <p className="text-sm text-muted-foreground">No hosted attempts are awaiting recovery.</p>}
      {rows.map(row => <div key={row.sessionId} className="border border-border p-3 space-y-2">
        <div className="flex flex-wrap items-center gap-2"><strong>{row.providerKey}</strong><StatusPill tone="attention">{row.status === 'AwaitingProviderOutcome' ? 'Awaiting provider outcome' : row.status}</StatusPill><span className="text-sm">{row.flowKind}</span></div>
        <p className="break-all text-sm">Attempt {row.sessionId} · {formatOperatorDate(row.createdDate)}</p>
        <div className="flex gap-2">
          <Button variant="outline" disabled={busy} onClick={() => void recover(row, 'refresh')}>Refresh from provider</Button>
          {row.status !== 'AwaitingProviderOutcome' && <Button variant="outline" disabled={busy} onClick={() => select(row)}>Review recovery</Button>}
        </div>
      </div>)}
      {selected && <form className="space-y-3 border border-border p-4" onSubmit={event => { event.preventDefault(); void recover(selected, 'reconcile') }}>
        <p className="font-medium">Recover {selected.providerKey} attempt {selected.sessionId}</p>
        <p className="text-sm text-muted-foreground">Check the provider account and subject correlation. For Veriff, only a terminal decision can prove a lost session belongs to this attempt. If a request may have been sent, reporting absence quarantines it and never creates a replacement automatically.</p>
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={absent} onChange={event => setAbsent(event.target.checked)} />Provider absence reviewed; quarantine an uncertain send</label>
        {!absent && <><Label htmlFor="hosted-provider-id">Provider session reference</Label><Input id="hosted-provider-id" value={providerId} onChange={event => setProviderId(event.target.value)} maxLength={256} required />
          {selected.providerKey === 'identomat' && <><Label htmlFor="hosted-redirect">Verified Identomat widget URL</Label><Input id="hosted-redirect" type="url" value={redirect} onChange={event => setRedirect(event.target.value)} maxLength={2048} required /></>}
        </>}
        <Label htmlFor="hosted-reason">Audit reason or evidence reference (no identity documents or secrets)</Label><Input id="hosted-reason" value={reason} onChange={event => setReason(event.target.value)} minLength={20} maxLength={1000} required />
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={reviewed} onChange={event => setReviewed(event.target.checked)} required />I reviewed the subject, provider account and evidence reference.</label>
        <div className="flex gap-2"><Button type="submit" disabled={busy || !reviewed || reason.trim().length < 20 || (!absent && !providerId.trim())}>{busy ? 'Verifying…' : 'Verify and record recovery'}</Button><Button type="button" variant="outline" disabled={busy} onClick={() => setSelected(null)}>Cancel</Button></div>
      </form>}
    </CardContent>
  </FlatCard>
}
