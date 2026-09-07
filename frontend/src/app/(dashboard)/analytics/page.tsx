'use client'

// Every row is currently unpriced BY DESIGN (the only IValueRateSource is UnpricedValueRateSource) — this page must look correct with zero priced rows.

/**
 * Analytics — D16 operation history + positions over the append-only value log.
 *
 * The one invariant this page exists to uphold: an absent fact renders as an
 * absent fact. An entry whose `pricing` is "NeverPriced" says "Unpriced", never
 * 0; an entry whose `amountStatus` is "Unknown" says "Unknown", never 0; a
 * position whose `isQuantityComplete` is false says "Incomplete" instead of a
 * partial total. Money strings are displayed verbatim — no client-side
 * arithmetic, ever. The backend exposes the status enums and the
 * unpriced/unknown counts precisely so the UI can show how much of the picture
 * is missing rather than fabricating a number.
 */

import { useState, useCallback, useEffect, useRef } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ErrorBanner } from '@/components/shared/error-banner'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoa } from '@/lib/azoa-context'
import type {
  AnalyticsQueryParams,
  PositionSummary,
  PositionsResponse,
  TransactionHistoryEntry,
  TransactionHistoryPage,
} from 'azoa-sdk'
import { BarChart3, ChevronLeft, ChevronRight, RefreshCw, AlertTriangle } from 'lucide-react'

const PAGE_SIZE = 25

function truncate(value: string, chars = 6): string {
  return value.length <= chars * 2 + 3 ? value : `${value.slice(0, chars)}…${value.slice(-chars)}`
}

/** Widens a yyyy-mm-dd pair into the inclusive ISO-8601 window the backend filters on. */
function buildWindow(fromDate: string, toDate: string): Pick<AnalyticsQueryParams, 'from' | 'to'> {
  return {
    from: fromDate ? `${fromDate}T00:00:00.000Z` : undefined,
    to: toDate ? `${toDate}T23:59:59.999Z` : undefined,
  }
}

/** A position's identity for React keys and test ids: chain + asset (native asset when absent). */
function positionKey(position: PositionSummary): string {
  return `${position.chainType ?? 'unknown-chain'}:${position.assetId ?? 'native'}`
}

/** Renders a transaction amount honouring `amountStatus` — "Unknown" is a fact, never 0. */
function AmountCell({ entry }: { entry: TransactionHistoryEntry }) {
  if (entry.amountStatus !== 'Known' || entry.amount === undefined) {
    return (
      <Badge variant="outline" className="text-[10px]" data-testid={`tx-amount-${entry.operationId}`}>
        Unknown
      </Badge>
    )
  }
  return (
    <span className="font-mono text-sm" data-testid={`tx-amount-${entry.operationId}`}>
      {entry.amount}
    </span>
  )
}

/** Renders a transaction value honouring `pricing` — "Unpriced" is a fact, never 0. */
function ValueCell({ entry }: { entry: TransactionHistoryEntry }) {
  if (entry.pricing === 'Priced' && entry.value) {
    return (
      <span className="font-mono text-sm" data-testid={`tx-value-${entry.operationId}`}>
        {entry.value.minorUnits} <span className="text-xs text-muted-foreground">{entry.value.currencyCode} (minor)</span>
      </span>
    )
  }
  return (
    <Badge variant="outline" className="text-[10px]" data-testid={`tx-value-${entry.operationId}`}>
      {entry.isStampable ? 'Unpriced' : 'Not stampable'}
    </Badge>
  )
}

/** Renders a position quantity — an incomplete quantity says so rather than showing a partial total. */
function QuantityCell({ position }: { position: PositionSummary }) {
  const key = positionKey(position)
  if (!position.isQuantityComplete) {
    return (
      <div className="space-y-0.5" data-testid={`pos-quantity-${key}`}>
        <Badge variant="outline" className="text-[10px] border-amber-400 text-amber-700 dark:text-amber-300">
          Incomplete
        </Badge>
        <p className="text-[11px] text-muted-foreground">
          {[
            position.unknownAmountEntryCount > 0 && `${position.unknownAmountEntryCount} unknown amount`,
            position.unsettledEntryCount > 0 && `${position.unsettledEntryCount} unsettled`,
            position.unclassifiedEntryCount > 0 && `${position.unclassifiedEntryCount} unclassified`,
          ].filter(Boolean).join(' · ')}
        </p>
      </div>
    )
  }
  return (
    <span className="font-mono text-sm" data-testid={`pos-quantity-${key}`}>
      {position.quantity}
    </span>
  )
}

/** Renders a position's realized fiat value — present only when every contributing entry was priced. */
function RealizedValueCell({ position }: { position: PositionSummary }) {
  const key = positionKey(position)
  if (position.isValueComplete && position.realizedValue) {
    return (
      <span className="font-mono text-sm" data-testid={`pos-value-${key}`}>
        {position.realizedValue.minorUnits}{' '}
        <span className="text-xs text-muted-foreground">{position.realizedValue.currencyCode} (minor)</span>
      </span>
    )
  }
  return (
    <div className="space-y-0.5" data-testid={`pos-value-${key}`}>
      <Badge variant="outline" className="text-[10px]">Unpriced</Badge>
      <p className="text-[11px] text-muted-foreground">
        {position.unpricedEntryCount} of {position.entryCount} unpriced
        {position.unstampableEntryCount > 0 && ` (${position.unstampableEntryCount} unstampable)`}
      </p>
    </div>
  )
}

export default function AnalyticsPage() {
  const { userId } = useAzoa()

  // ── window filters (applied on demand, not on every keystroke) ──
  const [fromDate, setFromDate] = useState('')
  const [toDate, setToDate] = useState('')
  const [applied, setApplied] = useState<Pick<AnalyticsQueryParams, 'from' | 'to'>>({ from: undefined, to: undefined })

  // ── positions ──
  const [positions, setPositions] = useState<PositionsResponse | null>(null)
  const [positionsBusy, setPositionsBusy] = useState(false)
  const [positionsError, setPositionsError] = useState<string | null>(null)

  // ── transaction history (paged) ──
  const [skip, setSkip] = useState(0)
  const [page, setPage] = useState<TransactionHistoryPage | null>(null)
  const [pageBusy, setPageBusy] = useState(false)
  const [pageError, setPageError] = useState<string | null>(null)

  // A late response for a superseded window/page must not overwrite the current one
  // (one counter per loader — the two requests race each other on mount).
  const positionsGeneration = useRef(0)
  const pageGeneration = useRef(0)

  const loadPositions = useCallback(async (range: Pick<AnalyticsQueryParams, 'from' | 'to'>) => {
    const requestGeneration = ++positionsGeneration.current
    setPositionsBusy(true)
    setPositionsError(null)
    const result = await azoa.api.getAnalyticsPositions(range)
    if (requestGeneration !== positionsGeneration.current) return
    if (isOk(result)) {
      setPositions(result.value)
    } else {
      setPositionsError(result.error.message)
    }
    setPositionsBusy(false)
  }, [])

  const loadPage = useCallback(async (range: Pick<AnalyticsQueryParams, 'from' | 'to'>, pageSkip: number) => {
    const requestGeneration = ++pageGeneration.current
    setPageBusy(true)
    setPageError(null)
    const result = await azoa.api.getAnalyticsTransactions({ ...range, skip: pageSkip, take: PAGE_SIZE })
    if (requestGeneration !== pageGeneration.current) return
    if (isOk(result)) {
      setPage(result.value)
    } else {
      setPageError(result.error.message)
    }
    setPageBusy(false)
  }, [])

  useEffect(() => {
    if (!userId) return
    void loadPositions(applied)
  }, [userId, applied, loadPositions])

  useEffect(() => {
    if (!userId) return
    void loadPage(applied, skip)
  }, [userId, applied, skip, loadPage])

  const applyWindow = useCallback(() => {
    setSkip(0)
    setApplied(buildWindow(fromDate, toDate))
  }, [fromDate, toDate])

  const clearWindow = useCallback(() => {
    setFromDate('')
    setToDate('')
    setSkip(0)
    setApplied({ from: undefined, to: undefined })
  }, [])

  const refresh = useCallback(() => {
    void loadPositions(applied)
    void loadPage(applied, skip)
  }, [applied, skip, loadPositions, loadPage])

  if (!userId) {
    return (
      <Card>
        <CardContent className="pt-6">
          <p className="text-sm text-muted-foreground text-center">Sign in to view analytics.</p>
        </CardContent>
      </Card>
    )
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Analytics</h1>
        <p className="text-sm text-muted-foreground">
          Operation history and positions over the value log. Missing facts are shown as missing —
          an unpriced entry is “Unpriced”, an unknown quantity is “Unknown”, never zero.
        </p>
      </div>

      {/* Window filters */}
      <Card>
        <CardContent className="pt-6">
          <div className="flex flex-wrap items-end gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="from">From</Label>
              <Input id="from" type="date" value={fromDate} onChange={e => setFromDate(e.target.value)} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="to">To</Label>
              <Input id="to" type="date" value={toDate} onChange={e => setToDate(e.target.value)} />
            </div>
            <Button size="sm" onClick={applyWindow}>Apply</Button>
            <Button size="sm" variant="ghost" onClick={clearWindow} disabled={!applied.from && !applied.to}>
              Clear
            </Button>
            <Button size="sm" variant="outline" onClick={refresh} disabled={positionsBusy || pageBusy}>
              <RefreshCw className="h-3.5 w-3.5 mr-1.5" />
              Refresh
            </Button>
          </div>
        </CardContent>
      </Card>

      {/* Positions summary */}
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm flex items-center justify-between">
            <span className="flex items-center gap-2">
              <BarChart3 className="h-4 w-4" /> Positions
            </span>
            {positions && (
              <span className="text-xs font-normal text-muted-foreground">
                {positions.scannedEntryCount} entries scanned
              </span>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent>
          {positionsError ? (
            <ErrorBanner message={positionsError} onRetry={() => loadPositions(applied)} />
          ) : !positions ? (
            <p className="text-sm text-muted-foreground">{positionsBusy ? 'Loading…' : 'No data.'}</p>
          ) : (
            <div className="space-y-3">
              {!positions.isComplete && (
                <div className="rounded-lg border border-amber-300 bg-amber-50 dark:border-amber-900 dark:bg-amber-950 p-3 text-sm text-amber-800 dark:text-amber-300 flex items-start gap-2">
                  <AlertTriangle className="h-4 w-4 mt-0.5 shrink-0" />
                  <p>Scan truncated — these positions are not final totals. Narrow the window to complete it.</p>
                </div>
              )}
              {positions.items.length === 0 ? (
                <p className="text-sm text-muted-foreground">No positions in this window.</p>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Chain</TableHead>
                      <TableHead>Asset</TableHead>
                      <TableHead className="text-right">Quantity (base units)</TableHead>
                      <TableHead className="text-right">Entries</TableHead>
                      <TableHead className="text-right">Realized value</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {positions.items.map((position) => (
                      <TableRow key={positionKey(position)} data-testid={`pos-row-${positionKey(position)}`}>
                        <TableCell className="text-xs">{position.chainType ?? '—'}</TableCell>
                        <TableCell className="text-xs font-mono">{position.assetId ?? 'native'}</TableCell>
                        <TableCell className="text-right"><QuantityCell position={position} /></TableCell>
                        <TableCell className="text-right text-sm">{position.entryCount}</TableCell>
                        <TableCell className="text-right"><RealizedValueCell position={position} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
            </div>
          )}
        </CardContent>
      </Card>

      {/* Transaction history */}
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm flex items-center justify-between">
            <span className="flex items-center gap-2">
              <BarChart3 className="h-4 w-4" /> Transaction history
            </span>
            {page && (
              <span className="flex items-center gap-2 text-xs font-normal">
                <Badge variant="secondary" className="text-[10px]" data-testid="tx-unpriced-count">
                  {page.unpricedEntryCount} of {page.items.length} unpriced
                </Badge>
                <Badge variant="secondary" className="text-[10px]" data-testid="tx-unknown-count">
                  {page.unknownAmountEntryCount} of {page.items.length} unknown amount
                </Badge>
              </span>
            )}
          </CardTitle>
        </CardHeader>
        <CardContent>
          {pageError ? (
            <ErrorBanner message={pageError} onRetry={() => loadPage(applied, skip)} />
          ) : !page ? (
            <p className="text-sm text-muted-foreground">{pageBusy ? 'Loading…' : 'No data.'}</p>
          ) : (
            <div className="space-y-3">
              {page.items.length === 0 ? (
                <p className="text-sm text-muted-foreground">No operations in this window.</p>
              ) : (
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Time</TableHead>
                      <TableHead>Operation</TableHead>
                      <TableHead>Status</TableHead>
                      <TableHead>Chain / Asset</TableHead>
                      <TableHead className="text-right">Amount (base units)</TableHead>
                      <TableHead className="text-right">Fee</TableHead>
                      <TableHead className="text-right">Value</TableHead>
                      <TableHead>Tx</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {page.items.map((entry) => (
                      <TableRow key={entry.operationId} data-testid={`tx-row-${entry.operationId}`}>
                        <TableCell className="text-xs whitespace-nowrap">
                          {new Date(entry.timestamp).toLocaleString()}
                        </TableCell>
                        <TableCell className="text-xs">{entry.operationType}</TableCell>
                        <TableCell>
                          <Badge variant="outline" className="text-[10px]">{entry.status}</Badge>
                        </TableCell>
                        <TableCell className="text-xs">
                          {entry.chainType ?? '—'}
                          {entry.assetId && <span className="font-mono text-muted-foreground"> / {entry.assetId}</span>}
                        </TableCell>
                        <TableCell className="text-right"><AmountCell entry={entry} /></TableCell>
                        <TableCell className="text-right font-mono text-xs text-muted-foreground">
                          {entry.fee ?? '—'}
                        </TableCell>
                        <TableCell className="text-right"><ValueCell entry={entry} /></TableCell>
                        <TableCell className="font-mono text-[11px] text-muted-foreground">
                          {entry.txHash ? truncate(entry.txHash) : '—'}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              <Separator />
              <div className="flex items-center justify-between text-xs text-muted-foreground">
                <span>
                  Showing {page.items.length === 0 ? 0 : page.skip + 1}–{page.skip + page.items.length}
                  {page.hasMore ? ' (more available)' : ''}
                </span>
                <span className="flex items-center gap-2">
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}
                    disabled={pageBusy || skip === 0}
                  >
                    <ChevronLeft className="h-3.5 w-3.5 mr-1" /> Previous
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => setSkip(skip + PAGE_SIZE)}
                    disabled={pageBusy || !page.hasMore}
                  >
                    Next <ChevronRight className="h-3.5 w-3.5 ml-1" />
                  </Button>
                </span>
              </div>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
