'use client'

// Node fee control: the published schedule is edited at its exact version; every change is in the public fee audit.
import { useEffect, useState } from 'react'
import type { NodeFeeAuditEntry, NodeFeeSchedule, NodeFeeScheduleEntry } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { FlatCard, OperatorError, OperatorLoading, OperatorPageHeader, formatOperatorDate } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'

const OPERATIONS = ['mint', 'transfer', 'swap', 'actionComplete', 'federationPublish'] as const
type Operation = (typeof OPERATIONS)[number]
type Draft = Record<Operation, { flatBaseUnits: string; bps: string }>

function toDraft(schedule: NodeFeeSchedule): Draft {
  const entry = (value: NodeFeeScheduleEntry) => ({ flatBaseUnits: value.flatBaseUnits, bps: String(value.bps) })
  return Object.fromEntries(OPERATIONS.map(op => [op, entry(schedule[op])])) as Draft
}

export default function OperatorFeesPage() {
  const schedule = useOperatorResource<NodeFeeSchedule>('node-governance/fee-schedule', 'The fee schedule could not be read.')
  const audit = useOperatorResource<NodeFeeAuditEntry[]>('node-governance/fee-audit?limit=20', 'The fee audit could not be read.')
  const mutation = useOperatorMutation()
  const [draft, setDraft] = useState<Draft | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => { if (schedule.data) setDraft(toDraft(schedule.data)) }, [schedule.data])

  const valid = draft !== null && OPERATIONS.every(op => /^\d{1,20}$/.test(draft[op].flatBaseUnits)
    && /^\d{1,5}$/.test(draft[op].bps) && Number(draft[op].bps) <= 10_000)

  async function save() {
    if (!draft || !schedule.data) return
    setNotice(null)
    const body: Record<string, unknown> = { expectedVersion: schedule.data.version }
    for (const op of OPERATIONS) body[op] = { flatBaseUnits: draft[op].flatBaseUnits, bps: Number(draft[op].bps) }
    const result = await mutation.run<NodeFeeSchedule>('node-governance/fee-schedule', 'PUT', body)
    if (!result) { await schedule.reload(); return }
    setNotice(`Published fee schedule version ${result.version}.`)
    await Promise.all([schedule.reload(), audit.reload()])
  }

  return (
    <div className="flex flex-col gap-8">
      <OperatorPageHeader eyebrow="Fees" title="Node fee schedule"
        description="What this node charges per operation, published to everyone. Amounts are base units; bps is basis points (100 = 1%). A stale version is refused and reloaded."
        onRefresh={() => void schedule.reload()} refreshing={schedule.loading} />
      {(schedule.error || mutation.error) && <OperatorError message={(mutation.error ?? schedule.error)!} onRetry={() => void schedule.reload()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      {!draft || !schedule.data ? <OperatorLoading label="Reading the fee schedule" /> : (
        <FlatCard>
          <CardHeader><CardTitle>Version {schedule.data.version}{schedule.data.updatedAt ? ` · ${formatOperatorDate(schedule.data.updatedAt)}` : ''}</CardTitle></CardHeader>
          <CardContent className="space-y-3 text-sm">
            {OPERATIONS.map(op => (
              <div key={op} className="grid grid-cols-3 items-center gap-3">
                <span>{op}</span>
                <Input aria-label={`${op} flat base units`} value={draft[op].flatBaseUnits} inputMode="numeric"
                  onChange={event => setDraft({ ...draft, [op]: { ...draft[op], flatBaseUnits: event.target.value } })} />
                <Input aria-label={`${op} bps`} value={draft[op].bps} inputMode="numeric"
                  onChange={event => setDraft({ ...draft, [op]: { ...draft[op], bps: event.target.value } })} />
              </div>
            ))}
            <Button disabled={mutation.busy || !valid} onClick={() => void save()}>Publish schedule</Button>
          </CardContent>
        </FlatCard>
      )}
      <FlatCard>
        <CardHeader><CardTitle>Recent changes</CardTitle></CardHeader>
        <CardContent className="space-y-2 text-sm">
          {audit.error && <OperatorError message={audit.error} />}
          {(audit.data ?? []).length === 0 ? <p className="text-muted-foreground">No recorded changes.</p> : (audit.data ?? []).map(entry => (
            <p key={`${entry.newVersion}:${entry.occurredAt}`}>{formatOperatorDate(entry.occurredAt)} · {entry.action} · v{entry.previousVersion} → v{entry.newVersion}</p>
          ))}
        </CardContent>
      </FlatCard>
    </div>
  )
}
