'use client'

// F-H12 deployment operator exit: re-observe first; close only with a reason and the no-further-effect acknowledgement.
import { useState } from 'react'
import type { DeploymentOperatorAction, DeploymentReceipt, ParkedDeployment } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { CardContent } from '@/components/ui/card'
import { FlatCard, OperatorError, OperatorLoading, OperatorPageHeader, StatusPill } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'

export default function OperatorDeploymentsPage() {
  const { data, error, loading, reload } = useOperatorResource<ParkedDeployment[]>('deployments?limit=100', 'Parked deployments could not be read.')
  const mutation = useOperatorMutation()
  const [selected, setSelected] = useState<ParkedDeployment | null>(null)
  const [reason, setReason] = useState('')
  const [acknowledged, setAcknowledged] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const rows = data ?? []

  async function resolve(row: ParkedDeployment, action: DeploymentOperatorAction) {
    setNotice(null)
    const body = action === 'Reobserve'
      ? { expectedVersion: row.version, action, acknowledgeNoFurtherEffect: false }
      : { expectedVersion: row.version, action, reason: reason.trim(), acknowledgeNoFurtherEffect: acknowledged }
    const result = await mutation.run<DeploymentReceipt>(`deployments/${row.operationId}/resolve`, 'POST', body)
    if (!result) return
    setNotice(result.state === 'NeedsOperator'
      ? `Still unresolved after re-observing: ${result.detail ?? 'no definitive chain verdict yet'}.`
      : `Deployment ${result.operationId} is now ${result.state}.`)
    setSelected(null)
    setReason('')
    setAcknowledged(false)
    await reload()
  }

  return (
    <div className="flex flex-col gap-8">
      <OperatorPageHeader eyebrow="Deployments" title="Native deployments parked for a person"
        description="A definitive chain verdict always wins: re-observing applies it. Closing is for a row whose unresolved wire can have no further effect, and is audited with your reason."
        onRefresh={() => void reload()} refreshing={loading} />
      {(error || mutation.error) && <OperatorError message={(mutation.error ?? error)!} onRetry={() => void reload()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {loading ? <OperatorLoading label="Reading parked deployments" /> : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">No deployment is parked for operator investigation.</p>
      ) : rows.map(row => (
        <FlatCard key={row.operationId}>
          <CardContent className="space-y-2 pt-6 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <strong>{row.target.chainType} {row.target.network}</strong>
              <StatusPill tone="attention">{row.state}</StatusPill>
              <span>stage {row.stage} · version {row.version}</span>
            </div>
            <p className="break-all">Operation {row.operationId}</p>
            {row.transactionIds.length > 0 && <p className="break-all">Transactions {row.transactionIds.join(', ')}</p>}
            {row.detail && <p className="text-muted-foreground">{row.detail}</p>}
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" disabled={mutation.busy} onClick={() => void resolve(row, 'Reobserve')}>Re-observe chain</Button>
              <Button variant="outline" disabled={mutation.busy} onClick={() => setSelected(row)}>Close as unresolvable…</Button>
            </div>
            {selected?.operationId === row.operationId && (
              <form className="space-y-2 border border-border p-3" onSubmit={event => { event.preventDefault(); void resolve(row, 'CloseUnresolvable') }}>
                <Label htmlFor="deploy-reason">Why the unresolved wire can have no further effect (at least 10 characters)</Label>
                <Input id="deploy-reason" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} />
                <label className="flex items-center gap-2"><input type="checkbox" checked={acknowledged} onChange={event => setAcknowledged(event.target.checked)} />I confirm the prepared transaction can no longer land.</label>
                <div className="flex gap-2">
                  <Button type="submit" disabled={mutation.busy || !acknowledged || reason.trim().length < 10}>Close deployment</Button>
                  <Button type="button" variant="outline" onClick={() => setSelected(null)}>Cancel</Button>
                </div>
              </form>
            )}
          </CardContent>
        </FlatCard>
      ))}
    </div>
  )
}
