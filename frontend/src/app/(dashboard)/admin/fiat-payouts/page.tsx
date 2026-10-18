'use client'

// Operator board for money on its way out (W8.1).
// Every figure here comes from the server-side payout ledger (`api/admin/fiat-payouts`, Operator
// policy); nothing on this page is derived in the browser, because a rounded number on a financial
// board is worse than no number. Amounts stay integers: money in minor units, units in base units,
// both formatted only for display.
import { useCallback, useEffect, useMemo, useState } from 'react'
import { isRecentOperatorLoginRequired, type FiatPayoutResolution, type FiatPayoutView } from 'azoa-sdk'
import { azoa, isOk } from '@/lib/azoa'
import { toast } from 'sonner'
import { describePayoutResolution } from '@/lib/operator-honesty'
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
import { StepUpBanner } from '@/components/shared/step-up-banner'
import { AlertTriangle, PlayCircle, RefreshCw, Undo2 } from 'lucide-react'

const PAYOUTS_PATH = '/api/admin/fiat-payouts'
const PAGE_PATH = '/admin/fiat-payouts'

/**
 * Operator-facing words for each durable status. The server's vocabulary is the ledger's; this is
 * the sentence a person reads at 2am, so it says what happened rather than what the column is
 * called.
 */
const STATUS_COPY: Record<string, { label: string; tone: 'open' | 'working' | 'done' | 'bad' }> = {
  Requested: { label: 'Claimed — nothing moved', tone: 'open' },
  UnitsCollected: { label: 'Balance taken, money not sent', tone: 'bad' },
  BankPending: { label: 'Bank transfer processing', tone: 'working' },
  Paid: { label: 'Sent to their bank', tone: 'done' },
  Reversed: { label: 'Bank sent it back', tone: 'bad' },
  Returned: { label: 'Cancelled, balance restored', tone: 'done' },
  Refused: { label: 'Refused before anything moved', tone: 'done' },
  NeedsOperator: { label: 'Needs a decision', tone: 'bad' },
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

function ageInHours(requestedAt: string, now: number): number {
  return Math.max(0, (now - new Date(requestedAt).getTime()) / 3_600_000)
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

/** The next step the server will take for a row, in plain words. Null when there is none. */
function nextStepLabel(status: string): string | null {
  if (status === 'Requested') return 'Take the balance'
  if (status === 'UnitsCollected') return 'Send the money'
  if (status === 'BankPending') return 'Check bank transfer'
  return null
}

export default function FiatPayoutsConsolePage() {
  useAzoaAuth()
  const [rows, setRows] = useState<FiatPayoutView[]>([])
  const [limit, setLimit] = useState('100')
  const [loading, setLoading] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reauthRequired, setReauthRequired] = useState(false)
  const [now, setNow] = useState(() => Date.now())
  const [setupUserId, setSetupUserId] = useState('')
  const [setupBusy, setSetupBusy] = useState(false)

  /** X-7: every mutating action below is Operator + `RecentNodeOperatorSession`; surface the step-up prompt instead of a bare toast. */
  const reportActionError = useCallback((sdkError: { message: string }) => {
    if (isRecentOperatorLoginRequired(sdkError)) setReauthRequired(true)
    else toast.error(sdkError.message)
  }, [])

  const fetchBoard = useCallback(async () => {
    setLoading(true); setError(null)
    const parsedLimit = Number(limit)
    const query = Number.isFinite(parsedLimit) && parsedLimit > 0 ? `?limit=${parsedLimit}` : ''
    const res = await azoa.api.request<FiatPayoutView[]>('GET', `${PAYOUTS_PATH}${query}`)
    if (isOk(res)) {
      setRows(res.value ?? [])
      setNow(Date.now())
    } else {
      setRows([])
      setError(res.error.message)
    }
    setLoading(false)
  }, [limit])

  useEffect(() => { fetchBoard() }, [fetchBoard])

  /** Audited exit for a parked or reversed cash-out; the node honours it only when re-read Stripe/chain evidence agrees. */
  const resolve = useCallback(async (row: FiatPayoutView, resolution: FiatPayoutResolution) => {
    const question = resolution === 'record-paid'
      ? 'What evidence shows the money reached their bank? (the node re-reads Stripe before recording it)'
      : 'Why should the balance be returned? (the node re-reads Stripe before returning anything)'
    const reason = window.prompt(question)?.trim()
    if (!reason) return
    setBusyId(row.id); setReauthRequired(false)
    try {
      const res = await azoa.api.resolveFiatPayout(row.id, { resolution, reason })
      if (isOk(res)) {
        const verdict = describePayoutResolution(res.value)
        if (verdict.tone === 'ok') toast.success(verdict.title, { description: verdict.detail })
        else toast.warning(verdict.title, { description: verdict.detail })
        await fetchBoard()
      } else {
        reportActionError(res.error)
      }
    } finally {
      setBusyId(null)
    }
  }, [fetchBoard, reportActionError])

  const act = useCallback(async (
    id: string,
    verb: 'advance' | 'return' | 'park' | 'restart' | 'recover',
    successMessage: string,
  ) => {
    const reason = verb === 'recover' ? window.prompt('Why should this failed bank payout be recovered?')?.trim() : 'Actioned from the operator board.'
    if (verb === 'recover' && !reason) return
    setBusyId(id); setReauthRequired(false)
    try {
      const res = await azoa.api.request<FiatPayoutView>(
        'POST', `${PAYOUTS_PATH}/${encodeURIComponent(id)}/${verb}`,
        verb === 'return' || verb === 'park' || verb === 'recover'
          ? { reason }
          : undefined,
      )
      if (isOk(res)) {
        toast.success(successMessage, res.value?.status ? { description: `The node reports the cash-out as ${res.value.status}.` } : undefined)
        await fetchBoard()
      } else {
        reportActionError(res.error)
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'The action failed')
    } finally {
      setBusyId(null)
    }
  }, [fetchBoard, reportActionError])

  /** RV-L1-01: the operator exit for a payout-account setup stuck past its Stripe replay window. */
  const resolveAccountSetup = useCallback(async () => {
    const userId = setupUserId.trim()
    if (!userId) return
    const reason = window.prompt('What should this account setup resolve to? (Stripe is re-read before attaching or releasing anything)')?.trim()
    if (!reason) return
    setSetupBusy(true); setReauthRequired(false)
    try {
      const res = await azoa.api.resolvePayoutAccountSetup(userId, reason)
      if (isOk(res)) {
        toast.success(
          res.value.outcome === 'attached' ? 'Attached the account Stripe already holds for this claim' : 'No account existed; the claim was released for a fresh setup',
          { description: res.value.accountId ? `Connected account ${res.value.accountId}.` : undefined },
        )
        setSetupUserId('')
        await fetchBoard()
      } else {
        reportActionError(res.error)
      }
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'That user id is not a valid account identifier.')
    } finally {
      setSetupBusy(false)
    }
  }, [setupUserId, fetchBoard, reportActionError])

  const totals = useMemo(() => rows.reduce(
    (acc, row) => ({
      inFlight: acc.inFlight + (row.status === 'Requested' || row.status === 'UnitsCollected' || row.status === 'BankPending'
        ? row.amountMinorUnits : 0),
      strandedCount: acc.strandedCount + (row.status === 'UnitsCollected' ? 1 : 0),
      needsPerson: acc.needsPerson + (row.status === 'NeedsOperator' ? 1 : 0),
      reversed: acc.reversed + (row.status === 'Reversed' ? 1 : 0),
    }),
    { inFlight: 0, strandedCount: 0, needsPerson: 0, reversed: 0 },
  ), [rows])

  const owedRows = rows.filter(row => Number(row.unitsOwedBaseUnits ?? '0') > 0)
  const currency = rows[0]?.currencyCode ?? 'USD'

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Money Going Out</h1>
        <p className="text-sm text-muted-foreground">
          Cash-outs in flight, sent back by a bank, or waiting on a decision. Each row is somebody
          turning their balance into money in their own account — and the state to watch is
          &ldquo;balance taken, money not sent&rdquo;, because that person currently has neither.
        </p>
      </div>

      {(totals.needsPerson > 0 || totals.strandedCount > 0) && (
        <Card className="border-destructive/60 bg-destructive/5">
          <CardHeader className="pb-3">
            <div className="flex items-center gap-2">
              <AlertTriangle className="h-5 w-5 shrink-0 text-destructive" />
              <CardTitle className="text-sm text-destructive">
                {totals.strandedCount > 0 && (
                  <>{totals.strandedCount} cash-out{totals.strandedCount === 1 ? ' has' : 's have'} taken
                  a balance without sending money</>
                )}
                {totals.strandedCount > 0 && totals.needsPerson > 0 && ' · '}
                {totals.needsPerson > 0 && (
                  <>{totals.needsPerson} need{totals.needsPerson === 1 ? 's' : ''} a decision</>
                )}
              </CardTitle>
            </div>
            <CardDescription>
              Send the money or give the balance back — leaving a row here does neither. A cash-out
              parks on purpose when a move was submitted and its outcome could not be confirmed:
              check the rail before acting, because repeating it blind could take the balance twice.
            </CardDescription>
          </CardHeader>
        </Card>
      )}

      {owedRows.length > 0 && (
        <Card className="border-amber-500/60 bg-amber-500/5">
          <CardHeader className="pb-3">
            <CardTitle className="text-sm">
              {owedRows.length} account{owedRows.length === 1 ? '' : 's'} owed their balance back
            </CardTitle>
            <CardDescription>
              These cash-outs took a balance the platform could neither pay for nor return. Restoring
              the balance is a deliberate operator action; the exact figure is on each row.
            </CardDescription>
          </CardHeader>
        </Card>
      )}

      <Card>
        <CardHeader className="pb-3"><CardTitle className="text-sm">Open right now</CardTitle></CardHeader>
        <CardContent>
          <div className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
            <div>
              <div className="text-muted-foreground text-xs">Money in flight</div>
              <div className="font-medium">{formatMoney(totals.inFlight, currency)}</div>
            </div>
            <div>
              <div className="text-muted-foreground text-xs">On the board</div>
              <div className="font-medium">{rows.length}</div>
            </div>
            <div>
              <div className="text-muted-foreground text-xs">Balance taken, unsent</div>
              <div className="font-medium">{totals.strandedCount}</div>
            </div>
            <div>
              <div className="text-muted-foreground text-xs">Sent back by a bank</div>
              <div className="font-medium">{totals.reversed}</div>
            </div>
          </div>
          <p className="mt-3 text-xs text-muted-foreground">
            A cash-out takes the balance first and sends the money second. That order is deliberate:
            the other way round, a crash would pay for a balance the platform never took.
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
                onKeyDown={e => e.key === 'Enter' && fetchBoard()}
              />
            </div>
            <Button onClick={fetchBoard} disabled={loading} size="sm" variant="outline">
              <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
              {loading ? 'Loading…' : 'Refresh'}
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Stuck account setup</CardTitle>
          <CardDescription>
            RV-L1-01: an account setup that outlived its Stripe replay window. The node re-reads Stripe&apos;s own
            listing first — attaches the account it finds for this claim, or releases the claim once Stripe proves
            none exists, so the next setup opens a fresh one.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1.5">
              <Label className="text-xs" htmlFor="setup-user-id">Account owner&apos;s user id</Label>
              <Input
                id="setup-user-id"
                className="w-72 font-mono text-xs"
                placeholder="00000000-0000-0000-0000-000000000000"
                value={setupUserId}
                onChange={e => setSetupUserId(e.target.value)}
                onKeyDown={e => e.key === 'Enter' && resolveAccountSetup()}
              />
            </div>
            <Button onClick={resolveAccountSetup} disabled={setupBusy || !setupUserId.trim()} size="sm" variant="outline">
              {setupBusy ? 'Resolving…' : 'Resolve setup'}
            </Button>
          </div>
        </CardContent>
      </Card>

      {error && <ErrorBanner message={error} onRetry={fetchBoard} />}
      {reauthRequired && <StepUpBanner returnTo={PAGE_PATH} />}

      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Cash-outs {!loading && `(${rows.length})`}</CardTitle>
        </CardHeader>
        <CardContent className="p-0">
          {loading ? (
            <div className="space-y-2 p-4">{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
          ) : rows.length === 0 ? (
            <p className="p-6 text-center text-sm text-muted-foreground">
              Nothing is on its way out that still needs watching.
            </p>
          ) : (
            <div className="overflow-x-auto">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>Asked</TableHead>
                    <TableHead className="text-right">Money</TableHead>
                    <TableHead className="text-right">Balance</TableHead>
                    <TableHead className="text-right">Past hold</TableHead>
                    <TableHead>Account</TableHead>
                    <TableHead>State</TableHead>
                    <TableHead className="text-right">Actions</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {rows.map(row => {
                    const hours = ageInHours(row.requestedAt, now)
                    const alarming = row.status === 'NeedsOperator' || row.status === 'UnitsCollected'
                    const busy = busyId === row.id
                    const owed = Number(row.unitsOwedBaseUnits ?? '0') > 0
                    const nextStep = nextStepLabel(row.status)
                    return (
                      <TableRow key={row.id} className={alarming ? 'bg-destructive/5' : undefined}>
                        <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
                          <span title={new Date(row.requestedAt).toLocaleString()}>{formatAge(hours)} ago</span>
                          {row.attemptCount > 0 && (
                            <div className="text-[11px]">{row.attemptCount} attempt{row.attemptCount === 1 ? '' : 's'}</div>
                          )}
                        </TableCell>
                        <TableCell className="text-right text-sm tabular-nums">
                          {formatMoney(row.amountMinorUnits, row.currencyCode)}
                          {row.reversedMinorUnits > 0 && (
                            <div className="text-[11px] text-destructive">
                              {formatMoney(row.reversedMinorUnits, row.currencyCode)} taken back
                            </div>
                          )}
                        </TableCell>
                        <TableCell className="text-right text-sm tabular-nums">
                          {formatUnits(row.quantityBaseUnits)}
                          {owed && (
                            <div className="text-[11px] text-destructive">
                              {formatUnits(row.unitsOwedBaseUnits)} owed back
                            </div>
                          )}
                        </TableCell>
                        <TableCell className="text-right text-xs text-muted-foreground tabular-nums">
                          {formatUnits(row.agedQuantityBaseUnits)}
                          <div className="text-[11px]">
                            {row.consumedLotCount === 0
                              ? 'all unaged'
                              : `${row.consumedLotCount} lot${row.consumedLotCount === 1 ? '' : 's'}`}
                          </div>
                        </TableCell>
                        <TableCell className="text-xs">
                          <div className="max-w-[9rem] truncate font-mono text-[11px]" title={row.subjectUserId}>
                            {row.subjectUserId}
                          </div>
                          {row.tenantId && (
                            <div className="text-[11px] text-muted-foreground">via a partner</div>
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
                            {nextStep && (
                              <Button
                                size="sm"
                                variant="outline"
                                disabled={busy}
                                onClick={() => act(row.id, 'advance', 'The step ran')}
                                title="Run the next step now instead of waiting for the queue"
                              >
                                <PlayCircle className="mr-1 h-3 w-3" />
                                {nextStep}
                              </Button>
                            )}
                            {(row.status === 'NeedsOperator' || row.status === 'Reversed') && (
                              <>
                                <Button size="sm" variant="outline" disabled={busy}
                                  onClick={() => resolve(row, 'record-paid')}
                                  title="Record it paid — only if Stripe shows the transfer and bank payout landed">
                                  Record paid
                                </Button>
                                <Button size="sm" variant="outline" disabled={busy}
                                  onClick={() => resolve(row, 'return-units')}
                                  title="Return the balance — only if Stripe shows no money left the platform">
                                  Return balance
                                </Button>
                              </>
                            )}
                            {(row.status === 'Requested' || row.status === 'UnitsCollected') && (
                              <Button
                                size="sm"
                                variant="outline"
                                disabled={busy}
                                onClick={() => act(row.id, 'return', 'The balance was given back')}
                                title="Cancel this cash-out and put the balance back"
                              >
                                <Undo2 className="mr-1 h-3 w-3" />
                                Give back
                              </Button>
                            )}
                            {row.status !== 'NeedsOperator' && !STATUS_COPY[row.status]?.label.includes('Sent to') && (
                              <Button
                                size="sm"
                                variant="ghost"
                                disabled={busy}
                                onClick={() => act(row.id, 'park', 'The cash-out is held for review')}
                                title="Stop the queue touching this one until a person decides"
                              >
                                Hold
                              </Button>
                            )}
                            {(row.status === 'NeedsOperator' || row.status === 'Reversed') && row.providerPayoutId && (
                              <Button size="sm" variant="outline" disabled={busy}
                                onClick={() => act(row.id, 'recover', 'Recovery reconciled')}>
                                Recover failed bank payout
                              </Button>
                            )}
                            {(row.status === 'Requested' || row.status === 'UnitsCollected') && (
                              <Button
                                size="sm"
                                variant="ghost"
                                disabled={busy}
                                onClick={() => act(row.id, 'restart', 'The cash-out was queued')}
                                title="Queue the durable cash-out for this row"
                              >
                                Queue
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
        &ldquo;Past hold&rdquo; is the part of a cash-out that came from a card payment old enough
        that it can no longer be charged back. The rest came from work or stake, which was never
        bought with money that could be taken back and so was never on hold at all.
      </p>
    </div>
  )
}
