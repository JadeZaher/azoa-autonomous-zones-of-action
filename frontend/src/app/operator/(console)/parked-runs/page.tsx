'use client'

// Operator console for runs parked awaiting settlement confirmation (ActionRunStatus.AwaitingReconciliation).
// Listing comes from the operator saga surface (parked steps whose gate is `recon:{nodeId}`); the
// re-probe is POST /api/action/runs/reconcile-sweep, which observes settlement truth and NEVER re-sends.
import { useCallback, useEffect, useMemo, useState } from 'react'
import { isOk } from '@/lib/azoa'
import { operatorResult } from '@/lib/operator-result'
import type { SagaStepView, WorkflowReconciliationSweep } from 'azoa-sdk'
import { toast } from 'sonner'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ErrorBanner } from '@/components/shared/error-banner'
import { describeSweep } from '@/lib/operator-honesty'
import { AlertTriangle, CheckCircle2, RefreshCw } from 'lucide-react'

/** The durable saga every action run maps onto (`ActionWorkflowSaga.Name`). */
const ACTION_SAGA_NAME = 'action-workflow'

/** Gate-id prefix the step handler parks a run under (`recon:{nodeId}`). */
const RECONCILE_GATE_PREFIX = 'recon:'

/** Mirrors the server default (ReconciliationOptions.OperationHardStuckAfterSeconds = 3600). */
const DEFAULT_HARD_STUCK_MINUTES = 60

interface ParkedRun {
  runId: string
  /** One parked step per unresolved node in the run. */
  steps: SagaStepView[]
  /** Oldest park time across the run's parked steps. */
  parkedSinceIso: string
  ageMinutes: number
  hardStuck: boolean
  lastError?: string
}

function isReconciliationPark(step: SagaStepView): boolean {
  return step.sagaName === ACTION_SAGA_NAME
    && typeof step.gateId === 'string'
    && step.gateId.startsWith(RECONCILE_GATE_PREFIX)
}

function nodeIdOf(step: SagaStepView): string {
  return step.gateId?.slice(RECONCILE_GATE_PREFIX.length) ?? step.stepName
}

function formatAge(minutes: number): string {
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${Math.round(minutes)}m`
  const hours = minutes / 60
  if (hours < 48) return `${hours.toFixed(1)}h`
  return `${(hours / 24).toFixed(1)}d`
}

/** Groups parked steps into one row per run and stamps the hard-stuck verdict. */
function groupParkedRuns(steps: SagaStepView[], hardStuckMinutes: number, now: number): ParkedRun[] {
  const byRun = new Map<string, SagaStepView[]>()
  for (const step of steps) {
    const existing = byRun.get(step.correlationKey)
    if (existing) existing.push(step)
    else byRun.set(step.correlationKey, [step])
  }

  const rows: ParkedRun[] = []
  for (const [runId, runSteps] of Array.from(byRun.entries())) {
    const oldest = runSteps.reduce((a, b) => (
      new Date(a.updatedAt).getTime() <= new Date(b.updatedAt).getTime() ? a : b
    ))
    const ageMinutes = Math.max(0, (now - new Date(oldest.updatedAt).getTime()) / 60_000)
    rows.push({
      runId,
      steps: runSteps,
      parkedSinceIso: oldest.updatedAt,
      ageMinutes,
      hardStuck: ageMinutes >= hardStuckMinutes,
      lastError: runSteps.find(s => s.lastError)?.lastError,
    })
  }

  return rows.sort((a, b) => b.ageMinutes - a.ageMinutes)
}

function SweepSummary({ sweep, parkedAfter }: { sweep: WorkflowReconciliationSweep; parkedAfter?: number }) {
  const totals = sweep.results.reduce(
    (acc, r) => ({
      confirmed: acc.confirmed + r.reconciledConfirmed,
      released: acc.released + r.releasedFailedOnChain,
      unresolved: acc.unresolved + r.stillIndeterminate,
    }),
    { confirmed: 0, released: 0, unresolved: 0 },
  )
  const verdict = describeSweep(sweep, parkedAfter)

  return (
    <Card className={verdict.tone === 'ok' ? 'border-primary/40 bg-primary/5' : 'border-destructive/40 bg-destructive/5'}>
      <CardHeader className="pb-3">
        <div className="flex items-center gap-2">
          {verdict.tone === 'ok'
            ? <CheckCircle2 className="h-5 w-5 shrink-0 text-primary" />
            : <AlertTriangle className="h-5 w-5 shrink-0 text-destructive" />}
          <CardTitle className="text-sm">{verdict.title}</CardTitle>
        </div>
        <CardDescription>{verdict.detail}</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-5">
          <div><div className="text-muted-foreground text-xs">Scanned</div><div className="font-medium">{sweep.scanned}</div></div>
          <div><div className="text-muted-foreground text-xs">Confirmed</div><div className="font-medium">{totals.confirmed}</div></div>
          <div><div className="text-muted-foreground text-xs">Released to retry</div><div className="font-medium">{totals.released}</div></div>
          <div><div className="text-muted-foreground text-xs">Still unresolved</div><div className="font-medium">{totals.unresolved}</div></div>
          <div><div className="text-muted-foreground text-xs">Errors / hard-stuck</div><div className="font-medium">{sweep.errors} / {sweep.hardStuck}</div></div>
        </div>
      </CardContent>
    </Card>
  )
}

export default function ParkedRunsConsolePage() {
  const [steps, setSteps] = useState<SagaStepView[]>([])
  const [limit, setLimit] = useState('200')
  const [hardStuckMinutes, setHardStuckMinutes] = useState(String(DEFAULT_HARD_STUCK_MINUTES))
  const [loading, setLoading] = useState(false)
  const [sweeping, setSweeping] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [sweep, setSweep] = useState<WorkflowReconciliationSweep | null>(null)
  const [now, setNow] = useState(() => Date.now())

  const [parkedAfterSweep, setParkedAfterSweep] = useState<number | undefined>(undefined)

  /** Re-reads the parked board; resolves to the number of parked runs, or undefined when the read failed. */
  const fetchParked = useCallback(async (): Promise<number | undefined> => {
    setLoading(true); setError(null)
    const parsedLimit = Number(limit)
    const boundedLimit = Number.isFinite(parsedLimit) && parsedLimit > 0 ? Math.min(Math.trunc(parsedLimit), 500) : 100
    const res = await operatorResult<SagaStepView[]>(`sagas/dead-letters?status=Parked&limit=${boundedLimit}`)
    let parkedRuns: number | undefined
    if (isOk(res)) {
      const parked = res.value.filter(isReconciliationPark)
      setSteps(parked)
      setNow(Date.now())
      parkedRuns = new Set(parked.map(step => step.correlationKey)).size
    } else {
      setSteps([])
      setError(res.error.message)
    }
    setLoading(false)
    return parkedRuns
  }, [limit])

  useEffect(() => { fetchParked() }, [fetchParked])

  const thresholdMinutes = useMemo(() => {
    const parsed = Number(hardStuckMinutes)
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : DEFAULT_HARD_STUCK_MINUTES
  }, [hardStuckMinutes])

  const rows = useMemo(
    () => groupParkedRuns(steps, thresholdMinutes, now),
    [steps, thresholdMinutes, now],
  )
  const hardStuckCount = rows.filter(r => r.hardStuck).length

  const runSweep = useCallback(async () => {
    setSweeping(true)
    try {
      const res = await operatorResult<WorkflowReconciliationSweep>('action/runs/reconcile-sweep', { method: 'POST', body: {} })
      if (isOk(res)) {
        const parkedAfter = await fetchParked()
        setSweep(res.value)
        setParkedAfterSweep(parkedAfter)
        const verdict = describeSweep(res.value, parkedAfter)
        if (verdict.tone === 'ok') toast.success(verdict.title)
        else if (verdict.tone === 'bad') toast.error(verdict.title)
        else toast.warning(verdict.title)
      } else {
        toast.error(res.error.message)
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Re-probe failed')
    } finally {
      setSweeping(false)
    }
  }, [fetchParked])

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Waiting on Settlement</h1>
        <p className="text-sm text-muted-foreground">
          Runs whose money move was sent but whose outcome could not be read back. They are held here on
          purpose — re-probing reads the outcome, it never re-sends the move.
        </p>
      </div>

      {hardStuckCount > 0 && (
        <Card className="border-destructive/60 bg-destructive/5">
          <CardHeader className="pb-3">
            <div className="flex items-center gap-2">
              <AlertTriangle className="h-5 w-5 shrink-0 text-destructive" />
              <CardTitle className="text-sm text-destructive">
                {hardStuckCount} run{hardStuckCount === 1 ? '' : 's'} past the {thresholdMinutes}-minute review threshold
              </CardTitle>
            </div>
            <CardDescription>
              The background sweep has already re-probed these and still cannot establish an outcome.
              Confirm the move manually before deciding — nothing here will resolve itself.
            </CardDescription>
          </CardHeader>
        </Card>
      )}

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Controls</CardTitle></CardHeader>
        <CardContent>
          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1.5">
              <Label className="text-xs">Fetch limit</Label>
              <Input
                className="w-24"
                type="number"
                min={1}
                value={limit}
                onChange={e => setLimit(e.target.value)}
                onKeyDown={e => e.key === 'Enter' && fetchParked()}
              />
            </div>
            <div className="space-y-1.5">
              <Label className="text-xs">Review threshold (min)</Label>
              <Input
                className="w-32"
                type="number"
                min={0}
                value={hardStuckMinutes}
                onChange={e => setHardStuckMinutes(e.target.value)}
              />
            </div>
            <Button onClick={fetchParked} disabled={loading} size="sm" variant="outline">
              {loading ? 'Loading…' : 'Refresh'}
            </Button>
            <Button onClick={runSweep} disabled={sweeping || rows.length === 0} size="sm">
              <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${sweeping ? 'animate-spin' : ''}`} />
              {sweeping ? 'Re-probing…' : 'Re-probe all'}
            </Button>
          </div>
          <p className="mt-3 text-xs text-muted-foreground">
            The background reconciliation sweep already runs this on a timer and alerts on anything past the
            server-side threshold. This button is the on-demand version of the same pass.
          </p>
        </CardContent>
      </Card>

      {error && <ErrorBanner message={error} onRetry={fetchParked} />}

      {sweep && <SweepSummary sweep={sweep} parkedAfter={parkedAfterSweep} />}

      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Waiting runs {!loading && `(${rows.length})`}</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          {loading ? (
            <div className="space-y-2 p-4">{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
          ) : rows.length === 0 ? (
            <p className="p-6 text-center text-sm text-muted-foreground">
              Nothing is waiting on settlement. Every run has a definite outcome.
            </p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Run</TableHead>
                  <TableHead>Waiting</TableHead>
                  <TableHead>Since</TableHead>
                  <TableHead>Unresolved steps</TableHead>
                  <TableHead>Attempts</TableHead>
                  <TableHead>Last error</TableHead>
                  <TableHead className="text-right">State</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map(row => {
                  const outcome = sweep?.results.find(r => r.runId === row.runId)
                  return (
                    <TableRow key={row.runId} className={row.hardStuck ? 'bg-destructive/5' : undefined}>
                      <TableCell className="max-w-[10rem] truncate font-mono text-[11px]" title={row.runId}>
                        {row.runId}
                      </TableCell>
                      <TableCell className="text-sm">{formatAge(row.ageMinutes)}</TableCell>
                      <TableCell className="text-xs text-muted-foreground">
                        {new Date(row.parkedSinceIso).toLocaleString()}
                      </TableCell>
                      <TableCell className="text-sm">
                        <span title={row.steps.map(nodeIdOf).join(', ')}>{row.steps.length}</span>
                      </TableCell>
                      <TableCell className="text-sm">
                        {row.steps.reduce((max, s) => Math.max(max, s.attemptCount), 0)}
                      </TableCell>
                      <TableCell
                        className="max-w-xs truncate font-mono text-[11px] text-destructive"
                        title={row.lastError ?? ''}
                      >
                        {row.lastError ?? '—'}
                      </TableCell>
                      <TableCell className="text-right">
                        <div className="flex justify-end gap-1.5">
                          {outcome && (
                            <Badge variant="secondary" className="text-[10px]">
                              {outcome.reconciledConfirmed}✓ / {outcome.releasedFailedOnChain}↺ / {outcome.stillIndeterminate}?
                            </Badge>
                          )}
                          <Badge variant={row.hardStuck ? 'destructive' : 'secondary'} className="text-[10px]">
                            {row.hardStuck ? 'Needs review' : 'Waiting'}
                          </Badge>
                        </div>
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
