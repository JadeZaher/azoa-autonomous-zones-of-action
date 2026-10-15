'use client'

// Operator console for node economic-rules transparency (see docs/NODE-TRANSPARENCY.md).
// This page is a *consumer* of the public, credential-free `api/node-transparency/*` surface —
// it renders exactly what a third party (another node, an auditor, an integrator) already sees
// when they call that API themselves, so the operator can confirm what this node discloses
// before and after a governance or fee-schedule change. It shows only the three economic-rules
// categories (governance parameters, fee schedule, treasury destinations) and their audit
// history. It deliberately does not surface the separate node conformance manifest
// (`/.well-known/azoa-node.json`) — that document answers a different question (federation/
// interop conformance) and must not be folded into this narrative. Nothing here is sponsor
// copy: this is a technical, operator-only view, reachable only through the ordinary
// authenticated dashboard.
import { useCallback, useEffect, useState } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ErrorBanner } from '@/components/shared/error-banner'
import { RefreshCw, ShieldCheck, ShieldQuestion } from 'lucide-react'

const BASE_PATH = '/api/node-transparency'

/** Mirrors `NodeFeeScheduleEntryResponse` (Models/Responses/NodeTransparencyResponses.cs). */
interface FeeScheduleEntryView {
  flatBaseUnits: string
  bps: number
}

/** Mirrors `PublicNodeFeeScheduleResponse`. */
interface FeeScheduleView {
  mint: FeeScheduleEntryView
  transfer: FeeScheduleEntryView
  swap: FeeScheduleEntryView
  actionComplete: FeeScheduleEntryView
  federationPublish: FeeScheduleEntryView
  version: number
  updatedAt: string | null
}

/** Mirrors `PublicNodeGovernanceParametersResponse`. */
interface GovernanceParametersView {
  allowedChains: string[] | null
  allowedAssetTypes: string[] | null
  version: number
  updatedAt: string | null
}

/** Mirrors `PublicNodeTreasuryDestinationResponse`. */
interface TreasuryDestinationView {
  chain: string
  network: string
  address: string
  version: number
  updatedAt: string | null
}

/** Mirrors `NodeTransparencySnapshotResponse`. */
interface EconomicRulesSnapshotView {
  governance: GovernanceParametersView
  feeSchedule: FeeScheduleView
  treasuryDestinations: TreasuryDestinationView[]
  lastUpdatedAt: string | null
  contentSha256: string
  cryptographicHistoryProofAvailable: boolean
}

/** Mirrors `NodeTransparencyPageResponse<T>`. */
interface AuditPageView<T> {
  items: T[]
  nextCursor: string | null
  contentSha256: string
  cryptographicHistoryProofAvailable: boolean
}

/** Mirrors `PublicNodeGovernanceAuditResponse`. */
interface GovernanceAuditRow {
  action: string
  previousVersion: number
  newVersion: number
  previousAllowedChains: string[] | null
  previousAllowedAssetTypes: string[] | null
  allowedChains: string[] | null
  allowedAssetTypes: string[] | null
  occurredAt: string
}

/** Mirrors `PublicNodeFeeAuditResponse`. */
interface FeeAuditRow {
  action: string
  previousVersion: number
  newVersion: number
  previousSchedule: FeeScheduleView | null
  schedule: FeeScheduleView
  occurredAt: string
}

/** Mirrors `PublicNodeTreasuryAuditResponse`. */
interface TreasuryAuditRow {
  action: string
  chain: string
  network: string
  previousVersion: number
  newVersion: number
  previousDestination: TreasuryDestinationView | null
  destination: TreasuryDestinationView
  occurredAt: string
}

const FEE_ROWS: { key: keyof FeeScheduleView; label: string }[] = [
  { key: 'mint', label: 'Mint' },
  { key: 'transfer', label: 'Transfer' },
  { key: 'swap', label: 'Swap' },
  { key: 'actionComplete', label: 'Action complete' },
  { key: 'federationPublish', label: 'Federation publish' },
]

function formatBps(bps: number): string {
  return `${(bps / 100).toLocaleString(undefined, { maximumFractionDigits: 2 })}%`
}

function formatWhen(iso: string | null): string {
  if (!iso) return 'never'
  const parsed = new Date(iso)
  return Number.isNaN(parsed.getTime()) ? iso : parsed.toLocaleString()
}

function ShortHash({ value }: { value: string }) {
  if (!value) return <span className="text-muted-foreground">—</span>
  const short = value.length > 16 ? `${value.slice(0, 10)}…${value.slice(-6)}` : value
  return <code title={value} className="rounded bg-muted px-1.5 py-0.5 text-xs">{short}</code>
}

function GovernanceCard({ governance }: { governance: GovernanceParametersView }) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">Governance parameters</CardTitle>
        <CardDescription>Which chains and asset types this node&apos;s governance currently allows.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        <div>
          <div className="text-muted-foreground text-xs">Allowed chains</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            {(governance.allowedChains ?? []).length === 0
              ? <span className="text-muted-foreground">none disclosed</span>
              : governance.allowedChains!.map(chain => (
                <Badge key={chain} variant="outline" className="text-[10px]">{chain}</Badge>
              ))}
          </div>
        </div>
        <div>
          <div className="text-muted-foreground text-xs">Allowed asset types</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            {(governance.allowedAssetTypes ?? []).length === 0
              ? <span className="text-muted-foreground">none disclosed</span>
              : governance.allowedAssetTypes!.map(type => (
                <Badge key={type} variant="outline" className="text-[10px]">{type}</Badge>
              ))}
          </div>
        </div>
        <div className="flex items-center justify-between text-xs text-muted-foreground">
          <span>Version {governance.version}</span>
          <span>Updated {formatWhen(governance.updatedAt)}</span>
        </div>
      </CardContent>
    </Card>
  )
}

function FeeScheduleTable({ schedule }: { schedule: FeeScheduleView }) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">Fee schedule</CardTitle>
        <CardDescription>Flat base units plus a bps rate this node charges per operation.</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Operation</TableHead>
                <TableHead>Flat (base units)</TableHead>
                <TableHead>Rate</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {FEE_ROWS.map(({ key, label }) => {
                const entry = schedule[key] as FeeScheduleEntryView
                return (
                  <TableRow key={key}>
                    <TableCell className="font-medium">{label}</TableCell>
                    <TableCell className="font-mono text-xs">{entry.flatBaseUnits}</TableCell>
                    <TableCell className="font-mono text-xs">{formatBps(entry.bps)}</TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        </div>
        <div className="mt-3 flex items-center justify-between text-xs text-muted-foreground">
          <span>Version {schedule.version}</span>
          <span>Updated {formatWhen(schedule.updatedAt)}</span>
        </div>
      </CardContent>
    </Card>
  )
}

function TreasuryTable({ destinations }: { destinations: TreasuryDestinationView[] }) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">Treasury destinations</CardTitle>
        <CardDescription>Where this node routes platform treasury funds.</CardDescription>
      </CardHeader>
      <CardContent>
        {destinations.length === 0 ? (
          <p className="text-sm text-muted-foreground">No treasury destinations disclosed.</p>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Chain</TableHead>
                  <TableHead>Network</TableHead>
                  <TableHead>Address</TableHead>
                  <TableHead>Version</TableHead>
                  <TableHead>Updated</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {destinations.map(destination => (
                  <TableRow key={`${destination.chain}:${destination.network}:${destination.address}`}>
                    <TableCell className="font-medium">{destination.chain}</TableCell>
                    <TableCell><Badge variant="outline" className="text-[10px]">{destination.network}</Badge></TableCell>
                    <TableCell className="font-mono text-xs">{destination.address}</TableCell>
                    <TableCell>{destination.version}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatWhen(destination.updatedAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function CheckpointStatus({ available }: { available: boolean | 'loading' }) {
  if (available === 'loading') return <Skeleton className="h-5 w-40" />
  if (available === true) {
    return (
      <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <ShieldCheck className="h-3.5 w-3.5 text-emerald-600" />
        Signed audit checkpoint available
      </span>
    )
  }
  return (
    <span className="flex items-center gap-1.5 text-xs text-muted-foreground">
      <ShieldQuestion className="h-3.5 w-3.5" />
      Signed audit checkpoint not enabled on this node — an expected state, not an outage
    </span>
  )
}

/** A cursor-paged audit table shared by the governance/fee/treasury tabs. */
function AuditTab<T extends { action: string; occurredAt: string }>({
  path,
  columns,
  renderRow,
}: {
  path: string
  columns: string[]
  renderRow: (row: T) => React.ReactNode
}) {
  const [rows, setRows] = useState<T[]>([])
  const [cursor, setCursor] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loaded, setLoaded] = useState(false)

  const load = useCallback(async (nextCursor: string | null, replace: boolean) => {
    setLoading(true); setError(null)
    const query = nextCursor ? `?limit=50&cursor=${encodeURIComponent(nextCursor)}` : '?limit=50'
    const res = await azoa.api.request<AuditPageView<T>>('GET', `${path}${query}`)
    if (isOk(res)) {
      setRows(prev => (replace ? res.value.items : [...prev, ...res.value.items]))
      setCursor(res.value.nextCursor)
    } else {
      setError(res.error.message)
    }
    setLoading(false)
    setLoaded(true)
  }, [path])

  useEffect(() => { if (!loaded) load(null, true) }, [loaded, load])

  return (
    <div className="space-y-3">
      {error && <ErrorBanner message={error} onRetry={() => load(null, true)} />}
      <div className="overflow-x-auto">
        <Table>
          <TableHeader>
            <TableRow>{columns.map(col => <TableHead key={col}>{col}</TableHead>)}</TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((row, i) => (
              <TableRow key={`${row.action}-${row.occurredAt}-${i}`}>{renderRow(row)}</TableRow>
            ))}
            {loaded && rows.length === 0 && !error && (
              <TableRow><TableCell colSpan={columns.length} className="text-center text-sm text-muted-foreground">No changes recorded yet.</TableCell></TableRow>
            )}
          </TableBody>
        </Table>
      </div>
      {!loaded && (
        <div className="space-y-2">
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-8 w-full" />
        </div>
      )}
      {cursor && (
        <Button variant="outline" size="sm" onClick={() => load(cursor, false)} disabled={loading}>
          {loading ? 'Loading…' : 'Load more'}
        </Button>
      )}
    </div>
  )
}

export default function NodeTransparencyConsolePage() {
  useAzoaAuth()
  const [snapshot, setSnapshot] = useState<EconomicRulesSnapshotView | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [checkpointAvailable, setCheckpointAvailable] = useState<boolean | 'loading'>('loading')

  const fetchSnapshot = useCallback(async () => {
    setLoading(true); setError(null)
    const res = await azoa.api.request<EconomicRulesSnapshotView>('GET', `${BASE_PATH}/current`)
    if (isOk(res)) {
      setSnapshot(res.value)
    } else {
      setSnapshot(null)
      setError(res.error.message)
    }
    setLoading(false)
  }, [])

  const fetchCheckpoint = useCallback(async () => {
    setCheckpointAvailable('loading')
    const res = await azoa.api.request<unknown>('GET', `${BASE_PATH}/audit/checkpoint`)
    setCheckpointAvailable(isOk(res))
  }, [])

  useEffect(() => { fetchSnapshot(); fetchCheckpoint() }, [fetchSnapshot, fetchCheckpoint])

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Node Economic-Rules Transparency</h1>
        <p className="text-sm text-muted-foreground">
          Exactly what <code className="rounded bg-muted px-1 py-0.5 text-xs">api/node-transparency/*</code> discloses
          publicly, without credentials, to anyone who asks — this node&apos;s governance parameters, fee schedule,
          treasury destinations, and their audit history. This is operator-only reading of a public surface, not a
          sponsor-facing page: no sponsor should ever be sent here as proof of anything about their own money. The
          separate node conformance manifest (<code className="rounded bg-muted px-1 py-0.5 text-xs">/.well-known/azoa-node.json</code>)
          answers a different question — federation/interop conformance — and is deliberately not shown here. See{' '}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">docs/NODE-TRANSPARENCY.md</code>.
        </p>
      </div>

      <Card>
        <CardContent className="flex flex-wrap items-center justify-between gap-3 pt-6">
          <div className="flex flex-wrap items-center gap-4 text-xs text-muted-foreground">
            <span>Snapshot last updated {formatWhen(snapshot?.lastUpdatedAt ?? null)}</span>
            <span className="flex items-center gap-1.5">Content hash <ShortHash value={snapshot?.contentSha256 ?? ''} /></span>
            <CheckpointStatus available={checkpointAvailable} />
          </div>
          <Button onClick={() => { fetchSnapshot(); fetchCheckpoint() }} disabled={loading} size="sm" variant="outline">
            <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
            {loading ? 'Loading…' : 'Refresh'}
          </Button>
        </CardContent>
      </Card>

      {error && <ErrorBanner message={error} onRetry={fetchSnapshot} />}

      {!snapshot && loading && (
        <div className="grid gap-4 md:grid-cols-2">
          <Skeleton className="h-48 w-full" />
          <Skeleton className="h-48 w-full" />
        </div>
      )}

      {snapshot && (
        <div className="grid gap-4 md:grid-cols-2">
          <GovernanceCard governance={snapshot.governance} />
          <TreasuryTable destinations={snapshot.treasuryDestinations} />
          <div className="md:col-span-2">
            <FeeScheduleTable schedule={snapshot.feeSchedule} />
          </div>
        </div>
      )}

      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-sm">Audit history</CardTitle>
          <CardDescription>Newest-first, cursor-paged changes to each disclosed category — the same rows a third party sees.</CardDescription>
        </CardHeader>
        <CardContent>
          <Tabs defaultValue="governance">
            <TabsList className="mb-3">
              <TabsTrigger value="governance">Governance</TabsTrigger>
              <TabsTrigger value="fees">Fee schedule</TabsTrigger>
              <TabsTrigger value="treasury">Treasury</TabsTrigger>
            </TabsList>
            <TabsContent value="governance">
              <AuditTab<GovernanceAuditRow>
                path={`${BASE_PATH}/audit/governance`}
                columns={['When', 'Version', 'Allowed chains', 'Allowed asset types']}
                renderRow={row => (
                  <>
                    <TableCell className="text-xs text-muted-foreground">{formatWhen(row.occurredAt)}</TableCell>
                    <TableCell>{row.previousVersion} → {row.newVersion}</TableCell>
                    <TableCell className="text-xs">{(row.allowedChains ?? []).join(', ') || '—'}</TableCell>
                    <TableCell className="text-xs">{(row.allowedAssetTypes ?? []).join(', ') || '—'}</TableCell>
                  </>
                )}
              />
            </TabsContent>
            <TabsContent value="fees">
              <AuditTab<FeeAuditRow>
                path={`${BASE_PATH}/audit/fees`}
                columns={['When', 'Version', 'Mint', 'Transfer', 'Swap']}
                renderRow={row => (
                  <>
                    <TableCell className="text-xs text-muted-foreground">{formatWhen(row.occurredAt)}</TableCell>
                    <TableCell>{row.previousVersion} → {row.newVersion}</TableCell>
                    <TableCell className="font-mono text-xs">{row.schedule.mint.flatBaseUnits} + {formatBps(row.schedule.mint.bps)}</TableCell>
                    <TableCell className="font-mono text-xs">{row.schedule.transfer.flatBaseUnits} + {formatBps(row.schedule.transfer.bps)}</TableCell>
                    <TableCell className="font-mono text-xs">{row.schedule.swap.flatBaseUnits} + {formatBps(row.schedule.swap.bps)}</TableCell>
                  </>
                )}
              />
            </TabsContent>
            <TabsContent value="treasury">
              <AuditTab<TreasuryAuditRow>
                path={`${BASE_PATH}/audit/treasury`}
                columns={['When', 'Version', 'Chain', 'Network', 'Address']}
                renderRow={row => (
                  <>
                    <TableCell className="text-xs text-muted-foreground">{formatWhen(row.occurredAt)}</TableCell>
                    <TableCell>{row.previousVersion} → {row.newVersion}</TableCell>
                    <TableCell>{row.chain}</TableCell>
                    <TableCell><Badge variant="outline" className="text-[10px]">{row.network}</Badge></TableCell>
                    <TableCell className="font-mono text-xs">{row.destination.address}</TableCell>
                  </>
                )}
              />
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>
    </div>
  )
}
