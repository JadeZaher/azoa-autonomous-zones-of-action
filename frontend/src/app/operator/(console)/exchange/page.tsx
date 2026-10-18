'use client'

// Retired exchange rows: the operator attests the chain outcome the ledger was made good for; applied only if reconciliation agrees.
import { useState } from 'react'
import type { RetiredExchangeResolution, RetiredExchangeRowView } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { CardContent } from '@/components/ui/card'
import { FlatCard, OperatorError, OperatorLoading, OperatorPageHeader, StatusPill, formatOperatorDate } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'

export default function OperatorExchangePage() {
  const { data, error, loading, reload } = useOperatorResource<RetiredExchangeRowView[]>('exchange/retired-rows?limit=100', 'Retired exchange rows could not be read.')
  const mutation = useOperatorMutation()
  const [selected, setSelected] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<RetiredExchangeResolution['outcome']>('Confirmed')
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const rows = data ?? []

  async function resolve(operationId: string) {
    setNotice(null)
    const result = await mutation.run<RetiredExchangeRowView>(`exchange/retired-rows/${operationId}/resolve`, 'POST', { outcome, reason: reason.trim() })
    if (!result) return
    setNotice(`Recorded ${result.resolvedOutcome ?? outcome} for ${result.operationId}.`)
    setSelected(null)
    setReason('')
    await reload()
  }

  return (
    <div className="flex flex-col gap-8">
      <OperatorPageHeader eyebrow="Exchange" title="Retired exchange rows"
        description="Rows from the retired exchange path that submitted a transaction. Record what you did to the ledger; the node applies it only when reconciliation’s chain verdict matches."
        onRefresh={() => void reload()} refreshing={loading} />
      {(error || mutation.error) && <OperatorError message={(mutation.error ?? error)!} onRetry={() => void reload()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {loading ? <OperatorLoading label="Reading retired rows" /> : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">No retired exchange row is waiting.</p>
      ) : rows.map(row => (
        <FlatCard key={row.operationId}>
          <CardContent className="space-y-2 pt-6 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <strong>{row.chainType ?? 'unknown chain'}</strong>
              <StatusPill tone={row.resolvedOutcome ? 'ready' : 'attention'}>{row.resolvedOutcome ?? row.status}</StatusPill>
              <span>{formatOperatorDate(row.createdAt)}</span>
            </div>
            <p className="break-all">Operation {row.operationId}{row.transactionHash ? ` · tx ${row.transactionHash}` : ''}</p>
            {row.resolvedReason && <p className="text-muted-foreground">{row.resolvedReason}</p>}
            {!row.resolvedOutcome && row.transactionHash && <Button variant="outline" disabled={mutation.busy} onClick={() => setSelected(row.operationId)}>Record outcome…</Button>}
            {selected === row.operationId && (
              <form className="space-y-2 border border-border p-3" onSubmit={event => { event.preventDefault(); void resolve(row.operationId) }}>
                <div className="flex gap-2">
                  {(['Confirmed', 'Failed'] as const).map(option => (
                    <Button key={option} type="button" size="sm" variant={outcome === option ? 'default' : 'outline'} onClick={() => setOutcome(option)}>{option}</Button>
                  ))}
                </div>
                <Label htmlFor="retired-reason">What you did to the ledger (at least 10 characters)</Label>
                <Input id="retired-reason" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} />
                <div className="flex gap-2">
                  <Button type="submit" disabled={mutation.busy || reason.trim().length < 10}>Record</Button>
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
