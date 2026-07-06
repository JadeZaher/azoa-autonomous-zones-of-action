'use client'

// Saga dead-letter operator console — inspect, requeue, and cancel stuck saga steps (SagaOperatorController).
import { useState, useCallback, useEffect } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import type { SagaStepView, SagaStepStatus } from '@azoa/sdk'
import { toast } from 'sonner'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogTrigger,
} from '@/components/ui/dialog'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ErrorBanner } from '@/components/shared/error-banner'

const STATUS_OPTIONS: SagaStepStatus[] = ['DeadLettered', 'Parked', 'Cancelled']

const statusBadgeVariant: Record<SagaStepStatus, 'default' | 'secondary' | 'destructive'> = {
  DeadLettered: 'destructive',
  Parked: 'secondary',
  Cancelled: 'default',
}

function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}…` : value
}

function LastErrorCell({ error }: { error?: string }) {
  const [expanded, setExpanded] = useState(false)
  if (!error) return <span className="text-muted-foreground">—</span>
  return (
    <button
      type="button"
      onClick={() => setExpanded(v => !v)}
      className="max-w-xs text-left font-mono text-[11px] text-destructive hover:underline"
      title={expanded ? 'Collapse' : 'Expand'}
    >
      {expanded ? error : truncate(error, 48)}
    </button>
  )
}

function CancelDialog({ step, onDone }: { step: SagaStepView; onDone: () => void }) {
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const [loading, setLoading] = useState(false)

  const handleCancel = async () => {
    setLoading(true)
    try {
      const res = await azoa.api.cancelSagaStep(step.id, reason || undefined)
      if (isOk(res)) {
        toast.success(res.value.message || 'Step cancelled')
        setOpen(false)
        onDone()
      } else {
        toast.error(res.error.message)
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to cancel step')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={<Button variant="destructive" size="sm">Cancel</Button>} />
      <DialogContent>
        <DialogHeader><DialogTitle>Cancel Saga Step</DialogTitle></DialogHeader>
        <p className="text-sm text-muted-foreground">
          This terminally cancels <span className="font-mono">{step.sagaName}/{step.stepName}</span>. It will never retry.
        </p>
        <div className="space-y-1.5">
          <Label>Reason (optional)</Label>
          <Input value={reason} onChange={e => setReason(e.target.value)} placeholder="Why is this being cancelled?" />
        </div>
        <Button variant="destructive" onClick={handleCancel} disabled={loading} size="sm" className="w-full">
          {loading ? 'Cancelling…' : 'Confirm Cancel'}
        </Button>
      </DialogContent>
    </Dialog>
  )
}

function RequeueButton({ step, onDone }: { step: SagaStepView; onDone: () => void }) {
  const [loading, setLoading] = useState(false)

  const handleRequeue = async () => {
    setLoading(true)
    try {
      const res = await azoa.api.requeueSagaStep(step.id)
      if (isOk(res)) {
        toast.success(res.value.message || 'Step requeued')
        onDone()
      } else {
        toast.error(res.error.message)
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to requeue step')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Button variant="outline" size="sm" onClick={handleRequeue} disabled={loading}>
      {loading ? 'Requeuing…' : 'Requeue'}
    </Button>
  )
}

export default function SagaConsolePage() {
  useAzoaAuth()
  const [status, setStatus] = useState<SagaStepStatus>('DeadLettered')
  const [limit, setLimit] = useState('100')
  const [steps, setSteps] = useState<SagaStepView[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const fetchSteps = useCallback(async () => {
    setLoading(true); setError(null)
    const parsedLimit = Number(limit)
    const res = await azoa.api.listSagaDeadLetters({
      status: [status],
      limit: Number.isFinite(parsedLimit) && parsedLimit > 0 ? parsedLimit : undefined,
    })
    if (isOk(res)) setSteps(res.value)
    else { setSteps([]); setError(res.error.message) }
    setLoading(false)
  }, [status, limit])

  useEffect(() => { fetchSteps() }, [fetchSteps])

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Saga Console</h1>
        <p className="text-sm text-muted-foreground">Inspect and operate on dead-lettered, parked, and cancelled saga steps</p>
      </div>

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Filters</CardTitle></CardHeader>
        <CardContent>
          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1.5">
              <Label className="text-xs">Status</Label>
              <Select value={status} onValueChange={v => setStatus((v ?? 'DeadLettered') as SagaStepStatus)}>
                <SelectTrigger className="w-40"><SelectValue /></SelectTrigger>
                <SelectContent>
                  {STATUS_OPTIONS.map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label className="text-xs">Limit</Label>
              <Input
                className="w-24"
                type="number"
                min={1}
                value={limit}
                onChange={e => setLimit(e.target.value)}
                onKeyDown={e => e.key === 'Enter' && fetchSteps()}
              />
            </div>
            <Button onClick={fetchSteps} disabled={loading} size="sm">{loading ? 'Loading…' : 'Refresh'}</Button>
          </div>
        </CardContent>
      </Card>

      {error && <ErrorBanner message={error} onRetry={fetchSteps} />}

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Steps {!loading && `(${steps.length})`}</CardTitle></CardHeader>
        <CardContent className="p-0">
          {loading ? (
            <div className="space-y-2 p-4">{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
          ) : steps.length === 0 ? (
            <p className="p-6 text-center text-sm text-muted-foreground">No saga steps match this filter.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Saga</TableHead>
                  <TableHead>Step</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Attempts</TableHead>
                  <TableHead>Correlation Key</TableHead>
                  <TableHead>Last Error</TableHead>
                  <TableHead>Updated</TableHead>
                  <TableHead className="text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {steps.map(step => (
                  <TableRow key={step.id}>
                    <TableCell className="text-sm font-medium">{step.sagaName}</TableCell>
                    <TableCell className="text-sm">{step.stepName}</TableCell>
                    <TableCell>
                      <Badge variant={statusBadgeVariant[step.status as SagaStepStatus] ?? 'secondary'} className="text-[10px]">
                        {step.status}
                      </Badge>
                    </TableCell>
                    <TableCell className="text-sm">{step.attemptCount}</TableCell>
                    <TableCell className="max-w-[8rem] truncate font-mono text-[11px] text-muted-foreground" title={step.correlationKey}>
                      {step.correlationKey}
                    </TableCell>
                    <TableCell><LastErrorCell error={step.lastError} /></TableCell>
                    <TableCell className="text-xs text-muted-foreground">{new Date(step.updatedAt).toLocaleString()}</TableCell>
                    <TableCell className="text-right">
                      <div className="flex justify-end gap-2">
                        {(step.status === 'DeadLettered' || step.status === 'Parked') && (
                          <RequeueButton step={step} onDone={fetchSteps} />
                        )}
                        {step.status !== 'Cancelled' && (
                          <CancelDialog step={step} onDone={fetchSteps} />
                        )}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
