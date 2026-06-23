'use client'

import { useState, useCallback } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { JsonViewer } from '@/components/shared/json-viewer'
import { azoa, isOk } from '@/lib/azoa'

// ─── Types ───

// SearchableEntityType flags (bitmask)
const ENTITY_TYPES = [
  { label: 'Avatar', value: 'Avatar', flag: 1 },
  { label: 'Holon', value: 'Holon', flag: 2 },
  { label: 'Wallet', value: 'Wallet', flag: 4 },
  { label: 'Blockchain Operation', value: 'BlockchainOperation', flag: 8 },
  { label: 'STAR ODK', value: 'STARODK', flag: 16 },
] as const

const SORT_FIELDS = ['CreatedDate', 'Name']
const PAGE_SIZES = [10, 20, 50]

interface SearchResultItem {
  entityType?: string
  id?: string
  name?: string
  [key: string]: unknown
}

interface SearchResponse {
  totalCount: number
  page: number
  pageSize: number
  results: SearchResultItem[]
  [key: string]: unknown
}

interface FacetData {
  [key: string]: unknown
}

function entityTypeBadgeClass(type: string) {
  switch (type?.toLowerCase()) {
    case 'avatar': return 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300'
    case 'holon': return 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300'
    case 'wallet': return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300'
    case 'blockchainoperation': return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300'
    case 'starodk': return 'bg-rose-100 text-rose-800 dark:bg-rose-900/30 dark:text-rose-300'
    default: return 'bg-muted text-muted-foreground'
  }
}

export default function SearchPage() {
  // Form state
  const [query, setQuery] = useState('')
  const [selectedTypes, setSelectedTypes] = useState<Set<string>>(new Set())
  const [chainId, setChainId] = useState('')
  const [assetType, setAssetType] = useState('')
  const [avatarId, setAvatarId] = useState('')
  const [sortBy, setSortBy] = useState('CreatedDate')
  const [sortDescending, setSortDescending] = useState(true)
  const [pageSize, setPageSize] = useState(10)
  const [page, setPage] = useState(1)

  // Results state
  const [results, setResults] = useState<SearchResponse | null>(null)
  const [facets, setFacets] = useState<FacetData | null>(null)
  const [expandedRow, setExpandedRow] = useState<number | null>(null)
  const [loading, setLoading] = useState(false)
  const [facetsLoading, setFacetsLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggleType = (value: string) => {
    setSelectedTypes((prev) => {
      const next = new Set(prev)
      if (next.has(value)) next.delete(value)
      else next.add(value)
      return next
    })
  }

  // .NET SearchableEntityType is a flags enum: Avatar=1, Holon=2, Wallet=4, BlockchainOperation=8, STARODK=16, All=31
  const entityTypeFlags: Record<string, number> = {
    Avatar: 1, Holon: 2, Wallet: 4, BlockchainOperation: 8, STARODK: 16,
  }
  const buildEntityTypes = (): number | undefined => {
    if (selectedTypes.size === 0) return undefined
    let flags = 0
    Array.from(selectedTypes).forEach((t) => { flags |= entityTypeFlags[t] ?? 0 })
    return flags
  }

  const doSearch = useCallback(
    async (targetPage = 1) => {
      setLoading(true)
      setError(null)
      try {
        const res = await azoa.api.search({
          query,
          entityTypes: buildEntityTypes(),
          chainId: chainId || undefined,
          assetType: assetType || undefined,
          avatarId: avatarId || undefined,
          sortBy,
          sortDescending,
          page: targetPage,
          pageSize,
        })
        if (isOk(res)) {
          setResults(res.value as unknown as SearchResponse)
          setPage(targetPage)
          setExpandedRow(null)
        } else {
          setError((res as { error: { message: string } }).error.message)
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Unknown error')
      } finally {
        setLoading(false)
      }
    },
    [query, selectedTypes, chainId, assetType, avatarId, sortBy, sortDescending, pageSize]
  )

  const loadFacets = async () => {
    setFacetsLoading(true)
    try {
      const res = await azoa.api.getSearchFacets()
      if (isOk(res)) {
        setFacets(res.value as unknown as FacetData)
      }
    } catch {
      // silent
    } finally {
      setFacetsLoading(false)
    }
  }

  const totalPages = results
    ? Math.ceil(results.totalCount / pageSize)
    : 0

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">Search</h1>
        <p className="text-sm text-muted-foreground">
          Search across avatars, holons, wallets, and blockchain operations
        </p>
      </div>

      <div className="grid gap-6 lg:grid-cols-[1fr_260px]">
        {/* Main Column */}
        <div className="space-y-6">
          {/* Search Form */}
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-sm">Search</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              {/* Query */}
              <div className="space-y-1.5">
                <Label>Query</Label>
                <Input
                  placeholder="Search query..."
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  onKeyDown={(e) => e.key === 'Enter' && doSearch(1)}
                />
              </div>

              {/* Entity Types */}
              <div className="space-y-2">
                <Label>Entity Types</Label>
                <div className="flex flex-wrap gap-3">
                  {ENTITY_TYPES.map((et) => (
                    <div key={et.value} className="flex items-center gap-1.5">
                      <Checkbox
                        id={`et-${et.value}`}
                        checked={selectedTypes.has(et.value)}
                        onCheckedChange={() => toggleType(et.value)}
                      />
                      <label
                        htmlFor={`et-${et.value}`}
                        className="text-sm cursor-pointer select-none"
                      >
                        {et.label}
                      </label>
                    </div>
                  ))}
                </div>
              </div>

              <Separator />

              {/* Optional Filters */}
              <div className="grid gap-3 sm:grid-cols-3">
                <div className="space-y-1.5">
                  <Label>Chain ID</Label>
                  <Input
                    placeholder="Optional"
                    value={chainId}
                    onChange={(e) => setChainId(e.target.value)}
                  />
                </div>
                <div className="space-y-1.5">
                  <Label>Asset Type</Label>
                  <Input
                    placeholder="Optional"
                    value={assetType}
                    onChange={(e) => setAssetType(e.target.value)}
                  />
                </div>
                <div className="space-y-1.5">
                  <Label>Avatar ID</Label>
                  <Input
                    placeholder="Optional"
                    value={avatarId}
                    onChange={(e) => setAvatarId(e.target.value)}
                  />
                </div>
              </div>

              {/* Sort & Page Size */}
              <div className="flex flex-wrap gap-3 items-end">
                <div className="space-y-1.5">
                  <Label>Sort By</Label>
                  <Select value={sortBy} onValueChange={(v) => setSortBy(v ?? 'CreatedDate')}>
                    <SelectTrigger className="w-40">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {SORT_FIELDS.map((f) => (
                        <SelectItem key={f} value={f}>{f}</SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>
                <div className="space-y-1.5">
                  <Label>Direction</Label>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="h-10 w-24"
                    onClick={() => setSortDescending((d) => !d)}
                  >
                    {sortDescending ? 'Desc' : 'Asc'}
                  </Button>
                </div>
                <div className="space-y-1.5">
                  <Label>Page Size</Label>
                  <Select
                    value={String(pageSize)}
                    onValueChange={(v) => setPageSize(Number(v))}
                  >
                    <SelectTrigger className="w-24">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {PAGE_SIZES.map((s) => (
                        <SelectItem key={s} value={String(s)}>{s}</SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </div>
                <Button onClick={() => doSearch(1)} disabled={loading} className="h-10">
                  {loading ? 'Searching...' : 'Search'}
                </Button>
              </div>

              {error && <p className="text-sm text-destructive">{error}</p>}
            </CardContent>
          </Card>

          {/* Results */}
          {results && (
            <Card>
              <CardHeader className="pb-3">
                <div className="flex items-center justify-between">
                  <CardTitle className="text-sm">
                    Results
                    <span className="ml-2 text-sm font-normal text-muted-foreground">
                      {results.totalCount} total
                    </span>
                  </CardTitle>
                  <span className="text-xs text-muted-foreground">
                    Page {page} of {totalPages || 1}
                  </span>
                </div>
              </CardHeader>
              <CardContent className="space-y-2">
                {results.results.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No results found.</p>
                ) : (
                  results.results.map((item, i) => (
                    <div key={i} className="rounded-md border">
                      <button
                        type="button"
                        className="w-full flex items-center gap-3 px-4 py-3 text-left hover:bg-muted/50 transition-colors"
                        onClick={() => setExpandedRow(expandedRow === i ? null : i)}
                      >
                        {item.entityType && (
                          <Badge
                            className={`shrink-0 text-xs ${entityTypeBadgeClass(item.entityType)}`}
                          >
                            {item.entityType}
                          </Badge>
                        )}
                        <span className="flex-1 truncate text-sm font-medium">
                          {item.name ?? item.id ?? `Result ${i + 1}`}
                        </span>
                        <span className="text-xs text-muted-foreground">
                          {expandedRow === i ? '▲' : '▼'}
                        </span>
                      </button>
                      {expandedRow === i && (
                        <div className="border-t px-4 py-3">
                          <div className="rounded-md bg-muted p-3 text-xs">
                            <JsonViewer data={item} />
                          </div>
                        </div>
                      )}
                    </div>
                  ))
                )}

                {/* Pagination */}
                {totalPages > 1 && (
                  <div className="flex items-center justify-between pt-2">
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={page <= 1 || loading}
                      onClick={() => doSearch(page - 1)}
                    >
                      Previous
                    </Button>
                    <span className="text-xs text-muted-foreground">
                      {page} / {totalPages}
                    </span>
                    <Button
                      size="sm"
                      variant="outline"
                      disabled={page >= totalPages || loading}
                      onClick={() => doSearch(page + 1)}
                    >
                      Next
                    </Button>
                  </div>
                )}
              </CardContent>
            </Card>
          )}
        </div>

        {/* Sidebar — Facets */}
        <div className="space-y-4">
          <Card>
            <CardHeader className="flex flex-row items-center justify-between pb-3">
              <CardTitle className="text-sm">Facets</CardTitle>
              <Button
                size="sm"
                variant="outline"
                onClick={loadFacets}
                disabled={facetsLoading}
              >
                {facetsLoading ? '...' : 'Load'}
              </Button>
            </CardHeader>
            <CardContent>
              {!facets ? (
                <p className="text-xs text-muted-foreground">
                  Click Load to fetch facet data.
                </p>
              ) : (
                <div className="text-xs">
                  <JsonViewer data={facets} />
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  )
}
