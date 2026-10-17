'use client'

// Operator reversal ledger; amounts are formatted from server-supplied minor/base units.
import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FiatReversalView } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { toast } from 'sonner'
import { useAzoaAuth } from '@/lib/azoa-auth'
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
import { AlertTriangle, RefreshCw, ShieldCheck, Undo2 } from 'lucide-react'

const REVERSALS_PATH = '/api/admin/fiat-reversals'

/** Display labels for durable reversal statuses. */
const STATUS_COPY: Record<string, { label: string; tone: 'open' | 'working' | 'done' | 'bad' }> = {
  Open: { label: 'Recorded — not started', tone: 'open' },
  Recovering: { label: 'Taking it back', tone: 'working' },
  Recovered: { label: 'Recovered in full', tone: 'done' },
  PartiallyRecovered: { label: 'Partly recovered', tone: 'bad' },
  Absorbed: { label: 'Platform absorbed it', tone: 'bad' },
  Reinstated: { label: 'Money came back', tone: 'done' },
  Unmatched: { label: 'Nothing to unwind', tone: 'done' },
  NeedsOperator: { label: 'Needs a decision', tone: 'bad' },
}

/** Which rung of the unwind answered — the same words the ledger records. */
const PATH_COPY: Record<string, string> = {
  None: 'not attempted',
  Balance: 'from the account balance',
  Hold: 'from an open commitment',
  Purchase: 'from what it bought',
  Reserve: 'absorbed by the platform',
  Mixed: 'from more than one place',
}

/** Money, from integer minor units. Never a float that reached the server. */
function formatMoney(minorUnits: number, currencyCode: string): string {
  const sign = minorUnits < 0 ? '-' : ''
  const absolute = Math.abs(minorUnits)
  const whole = Math.floor(absolute / 100)
  const cents = String(absolute % 100).padStart(2, '0')
  return `${sign}${whole.toLocaleString()}.${cents} ${currencyCode || ''}`.trim()
}

/** Base units at the platform unit's 2 decimals, kept as an exact string decomposition. */
function formatUnits(baseUnits: string): string {
  const digits = (baseUnits ?? '0').replace(/[^0-9]/g, '') || '0'
  const padded = digits.padStart(3, '0')
  const whole = padded.slice(0, -2).replace(/^0+(?=\d)/, '')
  const fraction = padded.slice(-2)
  return `${Number(whole).toLocaleString()}.${fraction}`
}

function ageInHours(openedAt: string, now: number): number {
  return Math.max(0, (now - new Date(openedAt).getTime()) / 3_600_000)
}

function formatAge(hours: number): string {
  if (hours < 1) return `${Math.round(hours * 60)}m`
  if (hours < 48) return `${hours.toFixed(1)}h`
  return `${(hours / 24).toFixed(1)}d`
}

function StatusBadge({ status }: { status: string }) {
  const copy = STATUS_COPY[status] ?? { label: status, tone: 'open' as const }
  const variant = copy.tone === 'bad' ? 'destructive' : copy.tone === 'done' ? 'secondary' : 'outline'
  return <Badge variant={variant} className="text-[10px] whitespace-nowrap">{copy.label}</Badge>
}

export default function FiatReversalsConsolePage() {
  useAzoaAuth()
  const [rows, setRows] = useState<FiatReversalView[]>([])
  const [limit, setLimit] = useState('100')
  const [loading, setLoading] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [now, setNow] = useState(() => Date.now())

  const fetchOpen = useCallback(async () => {
    setLoading(true); setError(null)
    const parsedLimit = Number(limit)
    const query = Number.isFinite(parsedLimit) && parsedLimit > 0 ? `?limit=${parsedLimit}` : ''
    const res = await azoa.api.request<FiatReversalView[]>('GET', `${REVERSALS_PATH}${query}`)
    if (isOk(res)) {
      setRows(res.value ?? [])
      setNow(Date.now())
    } else {
      setRows([])
      setError(res.error.message)
    }
    setLoading(false)
  }, [limit])

  useEffect(() => { fetchOpen() }, [fetchOpen])

  const act = useCallback(async (
    id: string,
    verb: 'recover' | 'settle' | 'restart' | 'clear-flag',
    successMessage: string,
  ) => {
    setBusyId(id)
    try {
      const res = await azoa.api.request<FiatReversalView>(
        'POST', `${REVERSALS_PATH}/${encodeURIComponent(id)}/${verb}`,
        verb === 'clear-flag' ? { reason: 'Lifted from the operator board.' } : undefined,
      )
      if (isOk(res)) {
        toast.success(successMessage)
        await fetchOpen()
      } else {
        toast.error(res.error.message)
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'The action failed')
    } finally {
      setBusyId(null)
    }
  }, [fetchOpen])

  const totals = useMemo(() => rows.reduce(
    (acc, row) => ({
      money: acc.money + row.amountMinorUnits,
      needsPerson: acc.needsPerson + (row.status === 'NeedsOperator' ? 1 : 0),
      flagged: acc.flagged + (row.accountFlagged ? 1 : 0),
      disputesPending: acc.disputesPending + (row.kind === 'Dispute' && !row.fundsWithdrawn ? 1 : 0),
    }),
    { money: 0, needsPerson: 0, flagged: 0, disputesPending: 0 },
  ), [rows])

  const currency = rows[0]?.currencyCode ?? 'USD'

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Money Taken Back</h1>
        <p className="text-sm text-muted-foreground">
          Payments that were refunded or disputed after this node had already turned them into units.
          Each row is what the platform is owed and how much of it has been recovered — the rest is a
          loss the platform is carrying.
        </p>
      </div>

      {totals.needsPerson > 0 && (
        <Card className="border-destructive/60 bg-destructive/5">
          <CardHeader className="pb-3">
            <div className="flex items-center gap-2">
              <AlertTriangle className="h-5 w-5 shrink-0 text-destructive" />
              <CardTitle className="text-sm text-destructive">
                {totals.needsPerson} reversal{totals.needsPerson === 1 ? '' : 's'} need a decision
              </CardTitle>
            </div>
            <CardDescription>
              The automatic unwind stopped on purpose — usually because a recovery was sent and its
              outcome could not be confirmed. Check it before doing anything else: repeating it blind
              could take back more than is owed.
            </CardDescription>
          </CardHeader>
        </Card>
      )}

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Open right now</CardTitle></CardHeader>
        <CardContent>
          <div className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
            <div>
              <div className="text-muted-foreground text-xs">Money taken back</div>
              <div className="font-medium">{formatMoney(totals.money, currency)}</div>
            </div>
            <div>
              <div className="text-muted-foreground text-xs">Open reversals</div>
              <div className="font-medium">{rows.length}</div>
            </div>
            <div>
              <div className="text-muted-foreground text-xs">Accounts paused</div>
              <div className="font-medium">{totals.flagged}</div>
            </div>
            <div>
              <div className="text-muted-foreground text-xs">Disputes not yet debited</div>
              <div className="font-medium">{totals.disputesPending}</div>
            </div>
          </div>
          <p className="mt-3 text-xs text-muted-foreground">
            A dispute that has not been debited yet pauses the account but takes nothing back. That is
            deliberate: it may still be resolved in the platform&apos;s favour.
          </p>
        </CardContent>
      </Card>

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
                onKeyDown={e => e.key === 'Enter' && fetchOpen()}
              />
            </div>
            <Button onClick={fetchOpen} disabled={loading} size="sm" variant="outline">
              <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
              {loading ? 'Loading…' : 'Refresh'}
            </Button>
          </div>
        </CardContent>
      </Card>

      {error && <ErrorBanner message={error} onRetry={fetchOpen} />}

      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Reversals {!loading && `(${rows.length})`}</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          {loading ? (
            <div className="space-y-2 p-4">{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
          ) : rows.length === 0 ? (
            <p className="p-6 text-center text-sm text-muted-foreground">
              Nothing has been taken back that is still open.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Opened</TableHead>
                    <TableHead>Kind</TableHead>
                    <TableHead className="text-right">Taken back</TableHead>
                    <TableHead className="text-right">Owed</TableHead>
                    <TableHead className="text-right">Recovered</TableHead>
                    <TableHead>How</TableHead>
                    <TableHead>Account</TableHead>
                    <TableHead>State</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map(row => {
                    const hours = ageInHours(row.openedAt, now)
                    const needsPerson = row.status === 'NeedsOperator'
                    const busy = busyId === row.id
                    return (
                      <TableRow key={row.id} className={needsPerson ? 'bg-destructive/5' : undefined}>
                        <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
                          <span title={new Date(row.openedAt).toLocaleString()}>{formatAge(hours)} ago</span>
                        </TableCell>
                        <TableCell className="text-sm">
                          <div>{row.kind === 'Dispute' ? 'Disputed' : 'Refunded'}</div>
                          <div className="text-[11px] text-muted-foreground">
                            {row.isPartial ? 'partial' : 'in full'}
                            {row.kind === 'Dispute' && !row.fundsWithdrawn ? ' · not debited yet' : ''}
                          </div>
                        </TableCell>
                        <TableCell className="text-right text-sm tabular-nums">
                          {formatMoney(row.amountMinorUnits, row.currencyCode)}
                        </TableCell>
                        <TableCell className="text-right text-sm tabular-nums">
                          {formatUnits(row.targetQuantityBaseUnits)}
                        </TableCell>
                        <TableCell className="text-right text-sm tabular-nums">
                          {formatUnits(row.recoveredQuantityBaseUnits)}
                          {Number(row.absorbedQuantityBaseUnits) > 0 && (
                            <div className="text-[11px] text-destructive">
                              {formatUnits(row.absorbedQuantityBaseUnits)} absorbed
                            </div>
                          )}
                        </TableCell>
                        <TableCell className="text-xs text-muted-foreground">
                          {PATH_COPY[row.recoveryPath] ?? row.recoveryPath}
                        </TableCell>
                        <TableCell className="text-xs">
                          <div className="max-w-[9rem] truncate font-mono text-[11px]" title={row.subjectUserId ?? ''}>
                            {row.subjectUserId ?? '—'}
                          </div>
                          {row.accountFlagged && (
                            <Badge variant="destructive" className="mt-1 text-[10px]">paused</Badge>
                          )}
                        </TableCell>
                        <TableCell>
                          <StatusBadge status={row.status} />
                          {row.statusReason && (
                            <div
                              className="mt-1 max-w-xs truncate text-[11px] text-muted-foreground"
                              title={row.statusReason}
                            >
                              {row.statusReason}
                            </div>
                          )}
                        </TableCell>
                        <TableCell className="text-right">
                          <div className="flex flex-wrap justify-end gap-1.5">
                            <Button
                              size="sm"
                              variant="outline"
                              disabled={busy}
                              onClick={() => act(row.id, 'recover', 'The unwind ran')}
                              title="Take back whatever is still outstanding, now"
                            >
                              <Undo2 className="mr-1 h-3 w-3" />
                              Unwind
                            </Button>
                            <Button
                              size="sm"
                              variant="outline"
                              disabled={busy}
                              onClick={() => act(row.id, 'settle', 'The reversal is closed')}
                              title="Close it: the platform carries whatever is left"
                            >
                              Close
                            </Button>
                            {row.status === 'Open' && (
                              <Button
                                size="sm"
                                variant="ghost"
                                disabled={busy}
                                onClick={() => act(row.id, 'restart', 'The unwind was queued')}
                                title="Queue the durable unwind for this reversal"
                              >
                                Queue
                              </Button>
                            )}
                            {row.accountFlagged && (
                              <Button
                                size="sm"
                                variant="ghost"
                                disabled={busy}
                                onClick={() => act(row.id, 'clear-flag', 'The pause was lifted')}
                                title="Let this account move value again"
                              >
                                <ShieldCheck className="mr-1 h-3 w-3" />
                                Unpause
                              </Button>
                            )}
                          </div>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </div>
          )}
        </CardContent>
      </Card>

      <p className="text-xs text-muted-foreground">
        Pausing an account stops it buying more units or moving value out while a reversal is open.
        Lifting the pause is recorded against whoever lifted it.
      </p>
    </div>
  )
}
