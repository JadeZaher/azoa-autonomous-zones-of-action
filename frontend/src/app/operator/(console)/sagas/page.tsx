'use client'

// Generic saga dead letters. Action-workflow steps are refused upstream and recovered under /operator/actions.
import { useState } from 'react'
import Link from 'next/link'
import type { SagaStepView } from 'azoa-sdk'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { CardContent } from '@/components/ui/card'
import { FlatCard, OperatorError, OperatorLoading, OperatorPageHeader, StatusPill, formatOperatorDate } from '@/components/operator/operator-ui'
import { useOperatorMutation, useOperatorResource } from '@/lib/use-operator-resource'

type Filter = 'DeadLettered' | 'Parked'

/** The server's maximum page (`sagaQuery` in operator-routes.ts); it has no saga-name filter, so workflow steps share the page. */
const PAGE_LIMIT = 500

export default function OperatorSagasPage() {
  const [filter, setFilter] = useState<Filter>('DeadLettered')
  const { data, error, loading, reload } = useOperatorResource<SagaStepView[]>(`sagas/dead-letters?status=${filter}&limit=${PAGE_LIMIT}`, 'Saga steps could not be read.')
  const mutation = useOperatorMutation()
  const [reason, setReason] = useState('')
  const [notice, setNotice] = useState<string | null>(null)
  const rows = (data ?? []).filter(step => step.sagaName !== 'action-workflow')
  const hiddenWorkflowSteps = (data?.length ?? 0) - rows.length
  const pageFull = (data?.length ?? 0) >= PAGE_LIMIT

  async function act(step: SagaStepView, verb: 'requeue' | 'cancel') {
    setNotice(null)
    const result = await mutation.run<{ status?: string; message?: string }>(`sagas/${step.id}/${verb}`, 'POST', verb === 'cancel' ? { reason: reason.trim() } : {})
    if (!result) return
    setNotice(result.message ?? `Step ${step.id} is now ${result.status ?? 'changed'}.`)
    await reload()
  }

  return (
    <div className="flex flex-col gap-8">
      <OperatorPageHeader eyebrow="Sagas" title="Saga steps that stopped"
        description="Requeue sends a step back to the processor; cancel stops it for good. Workflow runs are not here — they recover through audited action recovery."
        onRefresh={() => void reload()} refreshing={loading} />
      <div className="flex gap-2">
        {(['DeadLettered', 'Parked'] as const).map(option => (
          <Button key={option} size="sm" variant={option === filter ? 'default' : 'outline'} onClick={() => setFilter(option)}>{option}</Button>
        ))}
      </div>
      {(error || mutation.error) && <OperatorError message={(mutation.error ?? error)!} onRetry={() => void reload()} />}
      {notice && <p role="status" className="text-sm">{notice}</p>}
      <Input aria-label="Cancellation reason" className="max-w-xl" value={reason} maxLength={1000} onChange={event => setReason(event.target.value)} placeholder="Reason for cancelling (at least 10 characters)" />
      {!loading && data && (hiddenWorkflowSteps > 0 || pageFull) && (
        <p role="note" className="text-sm">
          {hiddenWorkflowSteps > 0 && <>{hiddenWorkflowSteps} workflow step(s) on this page are hidden here; recover them under <Link className="underline underline-offset-4" href="/operator/actions">Actions</Link>. </>}
          {pageFull && <>The node returned a full page of {PAGE_LIMIT}, so older steps may not be shown. Resolve these, then refresh.</>}
        </p>
      )}
      {loading ? <OperatorLoading label="Reading saga steps" /> : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          {pageFull ? 'No generic saga steps on this page.' : `No ${filter === 'DeadLettered' ? 'dead-lettered' : 'parked'} saga steps.`}
        </p>
      ) : rows.map(step => (
        <FlatCard key={step.id}>
          <CardContent className="space-y-2 pt-6 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <strong>{step.sagaName} · {step.stepName}</strong>
              <StatusPill tone="attention">{step.status}</StatusPill>
              <span>{step.attemptCount} attempt(s) · {formatOperatorDate(step.updatedAt)}</span>
            </div>
            <p className="break-all">Correlation {step.correlationKey}</p>
            {step.lastError && <p className="text-muted-foreground">{step.lastError}</p>}
            <div className="flex gap-2">
              <Button variant="outline" disabled={mutation.busy} onClick={() => void act(step, 'requeue')}>Requeue</Button>
              <Button variant="outline" disabled={mutation.busy || reason.trim().length < 10} onClick={() => void act(step, 'cancel')}>Cancel step</Button>
            </div>
          </CardContent>
        </FlatCard>
      ))}
    </div>
  )
}
