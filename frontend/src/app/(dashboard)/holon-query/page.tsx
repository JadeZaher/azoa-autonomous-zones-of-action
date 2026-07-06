'use client'

// Live query-builder harness against the holon store — composes a HolonQueryParams request via the SDK's fluent HolonQueryBuilder.
import { useState, useCallback, useMemo } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import type { HolonResult } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import { Separator } from '@/components/ui/separator'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from '@/components/ui/table'
import { ChainBadge } from '@/components/shared/chain-badge'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ErrorBanner } from '@/components/shared/error-banner'

// Query fields mirror HolonQueryParams (sdk/azoa-wallet/src/client/holon-query.ts), which mirrors .NET HolonQueryRequest.
interface QueryState {
  name: string
  avatarId: string
  providerName: string
  chainId: string
  assetType: string
  parentHolonId: string
  isActive: boolean | undefined
  metadataKey: string
  metadataValue: string
}

const DEFAULT_QUERY: QueryState = {
  name: '',
  avatarId: '',
  providerName: '',
  chainId: 'any',
  assetType: '',
  parentHolonId: '',
  isActive: undefined,
  metadataKey: '',
  metadataValue: '',
}

// metadata isn't a server-side filter field on HolonQueryRequest — applied client-side over the fetched result set.
function matchesMetadata(holon: HolonResult, key: string, value: string): boolean {
  if (!key.trim()) return true
  const meta = holon.metadata ?? {}
  const actual = meta[key.trim()]
  if (actual === undefined) return false
  if (!value.trim()) return true
  return actual.toLowerCase().includes(value.trim().toLowerCase())
}

function QueryBuilderForm({ query, onChange, onSubmit, loading }: {
  query: QueryState
  onChange: (q: QueryState) => void
  onSubmit: () => void
  loading: boolean
}) {
  const set = <K extends keyof QueryState>(field: K) => (val: QueryState[K]) => onChange({ ...query, [field]: val })
  return (
    <Card>
      <CardHeader className="pb-3"><CardTitle className="text-sm">Query Builder</CardTitle></CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          <div className="space-y-1.5"><Label className="text-xs">Name</Label><Input placeholder="Contains…" value={query.name} onChange={e => set('name')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
          <div className="space-y-1.5"><Label className="text-xs">Avatar ID</Label><Input placeholder="owner uuid" value={query.avatarId} onChange={e => set('avatarId')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
          <div className="space-y-1.5"><Label className="text-xs">Provider</Label><Input placeholder="e.g. InMemory" value={query.providerName} onChange={e => set('providerName')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
          <div className="space-y-1.5"><Label className="text-xs">Chain</Label>
            <Select value={query.chainId} onValueChange={v => set('chainId')(v ?? 'any')}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent><SelectItem value="any">Any</SelectItem><SelectItem value="algorand">Algorand</SelectItem><SelectItem value="solana">Solana</SelectItem></SelectContent>
            </Select>
          </div>
          <div className="space-y-1.5"><Label className="text-xs">Asset Type</Label><Input placeholder="e.g. NFT" value={query.assetType} onChange={e => set('assetType')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
          <div className="space-y-1.5"><Label className="text-xs">Parent Holon ID</Label><Input placeholder="parent uuid" value={query.parentHolonId} onChange={e => set('parentHolonId')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
          <div className="space-y-1.5"><Label className="text-xs">Metadata Key</Label><Input placeholder="key (client-side)" value={query.metadataKey} onChange={e => set('metadataKey')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
          <div className="space-y-1.5"><Label className="text-xs">Metadata Value</Label><Input placeholder="value contains…" value={query.metadataValue} onChange={e => set('metadataValue')(e.target.value)} onKeyDown={e => e.key === 'Enter' && onSubmit()} /></div>
        </div>
        <div className="mt-3 flex items-center gap-4">
          <div className="flex items-center gap-2"><Checkbox id="isActive" checked={query.isActive === true} onCheckedChange={c => set('isActive')(c === true ? true : undefined)} /><Label htmlFor="isActive" className="text-xs">Active only</Label></div>
          <div className="ml-auto flex gap-2">
            <Button onClick={onSubmit} disabled={loading} size="sm">{loading ? 'Running…' : 'Run Query'}</Button>
            <Button variant="outline" size="sm" onClick={() => onChange(DEFAULT_QUERY)}>Reset</Button>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

function DetailPanel({ holon }: { holon: HolonResult }) {
  return (
    <Card className="h-fit">
      <CardHeader className="pb-3">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0"><CardTitle className="text-sm truncate">{holon.name}</CardTitle><p className="mt-0.5 truncate text-[11px] text-muted-foreground font-mono">{holon.id}</p></div>
          <ChainBadge chain={holon.chainId ?? 'unknown'} />
        </div>
      </CardHeader>
      <CardContent>
        <Tabs defaultValue="details">
          <TabsList className="mb-3"><TabsTrigger value="details">Details</TabsTrigger><TabsTrigger value="metadata">Metadata</TabsTrigger><TabsTrigger value="raw">Raw</TabsTrigger></TabsList>
          <TabsContent value="details" className="space-y-3">
            <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs">
              <span className="text-muted-foreground">Provider</span><span>{holon.providerName || '—'}</span>
              <span className="text-muted-foreground">Asset Type</span><span>{holon.assetType || '—'}</span>
              <span className="text-muted-foreground">Parent</span><span className="truncate font-mono">{holon.parentHolonId || '—'}</span>
              <span className="text-muted-foreground">Avatar</span><span className="truncate font-mono">{holon.avatarId || '—'}</span>
              <span className="text-muted-foreground">Status</span><span><Badge variant={holon.isActive ? 'default' : 'secondary'} className="text-[10px]">{holon.isActive ? 'Active' : 'Inactive'}</Badge></span>
              <span className="text-muted-foreground">Created</span><span>{holon.createdDate ? new Date(holon.createdDate).toLocaleDateString() : '—'}</span>
            </div>
          </TabsContent>
          <TabsContent value="metadata" className="space-y-1.5">
            {!holon.metadata || Object.keys(holon.metadata).length === 0
              ? <p className="text-xs text-muted-foreground">No metadata.</p>
              : Object.entries(holon.metadata).map(([k, v]) => (
                <div key={k} className="flex items-center justify-between gap-2 text-xs">
                  <span className="font-mono text-muted-foreground">{k}</span><span className="truncate">{typeof v === 'string' ? v : JSON.stringify(v)}</span>
                </div>
              ))}
          </TabsContent>
          <TabsContent value="raw">
            <Separator className="mb-3" />
            <div className="rounded-md bg-muted/50 p-3 text-xs"><JsonViewer data={holon} /></div>
          </TabsContent>
        </Tabs>
      </CardContent>
    </Card>
  )
}

export default function HolonQueryPage() {
  useAzoaAuth()
  const [query, setQuery] = useState<QueryState>(DEFAULT_QUERY)
  const [results, setResults] = useState<HolonResult[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [ran, setRan] = useState(false)
  const [selected, setSelected] = useState<HolonResult | null>(null)

  const runQuery = useCallback(async () => {
    setLoading(true); setError(null); setSelected(null)
    const params: Record<string, unknown> = {}
    if (query.name) params.name = query.name
    if (query.avatarId) params.avatarId = query.avatarId
    if (query.providerName) params.providerName = query.providerName
    if (query.chainId !== 'any') params.chainId = query.chainId
    if (query.assetType) params.assetType = query.assetType
    if (query.parentHolonId) params.parentHolonId = query.parentHolonId
    if (query.isActive !== undefined) params.isActive = query.isActive

    const result = await azoa.holons.where(params).execute()
    setLoading(false); setRan(true)
    if (isOk(result)) setResults(result.value)
    else { setResults([]); setError(result.error.message) }
  }, [query])

  const filtered = useMemo(
    () => results.filter(h => matchesMetadata(h, query.metadataKey, query.metadataValue)),
    [results, query.metadataKey, query.metadataValue]
  )

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight">Holon Query</h1>
        <p className="text-sm text-muted-foreground">Compose a live query against the holon store and inspect matches</p>
      </div>

      <QueryBuilderForm query={query} onChange={setQuery} onSubmit={runQuery} loading={loading} />

      {error && <ErrorBanner message={error} onRetry={runQuery} />}

      <div className="flex gap-6">
        <div className="min-w-0 flex-1">
          <Card>
            <CardHeader className="pb-3"><CardTitle className="text-sm">Results {ran && !loading && `(${filtered.length})`}</CardTitle></CardHeader>
            <CardContent className="p-0">
              {loading ? <div className="space-y-2 p-4">{[...Array(4)].map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}</div>
              : !ran ? <p className="p-6 text-center text-sm text-muted-foreground">Build a query above and run it.</p>
              : filtered.length === 0 ? <p className="p-6 text-center text-sm text-muted-foreground">No holons matched.</p>
              : (
                <Table>
                  <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Chain</TableHead><TableHead>Type</TableHead><TableHead>Status</TableHead><TableHead>ID</TableHead></TableRow></TableHeader>
                  <TableBody>{filtered.map(h => (
                    <TableRow key={h.id} className={`cursor-pointer ${selected?.id === h.id ? 'bg-accent' : ''}`} onClick={() => setSelected(h)}>
                      <TableCell className="font-medium text-sm">{h.name}</TableCell>
                      <TableCell><ChainBadge chain={h.chainId ?? 'unknown'} /></TableCell>
                      <TableCell className="text-xs">{h.assetType || '—'}</TableCell>
                      <TableCell><Badge variant={h.isActive ? 'default' : 'secondary'} className="text-[10px]">{h.isActive ? 'Active' : 'Inactive'}</Badge></TableCell>
                      <TableCell className="max-w-[8rem] truncate font-mono text-[11px] text-muted-foreground">{h.id}</TableCell>
                    </TableRow>
                  ))}</TableBody>
                </Table>
              )}
            </CardContent>
          </Card>
        </div>
        {selected && <div className="w-80 shrink-0"><DetailPanel holon={selected} /></div>}
      </div>
    </div>
  )
}
