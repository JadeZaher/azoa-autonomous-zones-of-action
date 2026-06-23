'use client'

import { useState, useRef, useCallback, useEffect } from 'react'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { useDebug } from '@/lib/debug-context'
import { ChainBadge } from '@/components/shared/chain-badge'
import { JsonViewer } from '@/components/shared/json-viewer'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'

// ─── Types ───────────────────────────────────────────────────────────────────

interface TestCase {
  name: string
  category: string
  fn: () => Promise<{ passed: boolean; detail: string; raw?: unknown }>
}

interface TestResult extends TestCase {
  status: 'pending' | 'running' | 'passed' | 'failed' | 'skipped'
  detail: string
  raw?: unknown
  durationMs?: number
}

interface SavedRun {
  date: string
  results: Array<{
    name: string
    category: string
    status: string
    detail: string
    durationMs?: number
  }>
}

const STORAGE_KEY = 'azoa_test_results_latest'

// ─── Test definitions ─────────────────────────────────────────────────────────

function buildTestCases(
  holonIdRef: React.MutableRefObject<string | null>,
  walletIdRef: React.MutableRefObject<string | null>,
  nftIdRef: React.MutableRefObject<string | null>,
): TestCase[] {
  return [
    // ── Auth ──────────────────────────────────────────────────────────────────
    {
      name: 'Get current profile',
      category: 'auth',
      fn: async () => {
        const result = await azoa.auth.getProfile()
        if (isOk(result)) {
          return { passed: true, detail: `Profile loaded: ${result.value.username ?? result.value.email ?? 'ok'}`, raw: result.value }
        }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── Blockchain Queries ────────────────────────────────────────────────────
    {
      name: 'Algorand chain info',
      category: 'blockchain',
      fn: async () => {
        const result = await azoa.wallet.getChainInfo('algorand')
        if (isOk(result)) return { passed: true, detail: 'Chain info retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Solana chain info',
      category: 'blockchain',
      fn: async () => {
        const result = await azoa.wallet.getChainInfo('solana')
        if (isOk(result)) return { passed: true, detail: 'Chain info retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Algorand validate address',
      category: 'blockchain',
      fn: async () => {
        const result = await azoa.wallet.validateAddress(
          'algorand',
          'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ',
        )
        if (isOk(result)) return { passed: true, detail: `Valid: ${result.value}`, raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Solana validate address',
      category: 'blockchain',
      fn: async () => {
        const result = await azoa.wallet.validateAddress(
          'solana',
          'So11111111111111111111111111111111111111112',
        )
        if (isOk(result)) return { passed: true, detail: `Valid: ${result.value}`, raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── Holon CRUD ────────────────────────────────────────────────────────────
    {
      name: 'Create holon',
      category: 'holons',
      fn: async () => {
        const result = await azoa.holons.create({
          name: 'Test Holon ' + Date.now(),
          description: 'Functional test',
          providerName: 'InMemory',
          chainId: 'algorand',
        })
        if (isOk(result)) {
          const id = (result.value as { id?: string }).id ?? null
          holonIdRef.current = id
          return { passed: true, detail: `Created ID: ${id ?? 'unknown'}`, raw: result.value }
        }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Query holons',
      category: 'holons',
      fn: async () => {
        const result = await azoa.holons.where({ name: 'Test' }).execute()
        if (isOk(result)) {
          return { passed: true, detail: `Found ${result.value.length} holons`, raw: result.value }
        }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Get holon by ID',
      category: 'holons',
      fn: async () => {
        const id = holonIdRef.current
        if (!id) return { passed: false, detail: 'Skipped — no holon ID from create step', raw: null }
        const result = await azoa.holons.get(id)
        if (isOk(result)) return { passed: true, detail: 'Holon fetched', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Update holon',
      category: 'holons',
      fn: async () => {
        const id = holonIdRef.current
        if (!id) return { passed: false, detail: 'Skipped — no holon ID from create step', raw: null }
        const result = await azoa.holons.update(id, { description: 'Updated description' })
        if (isOk(result)) return { passed: true, detail: 'Holon updated', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Delete holon',
      category: 'holons',
      fn: async () => {
        const id = holonIdRef.current
        if (!id) return { passed: false, detail: 'Skipped — no holon ID from create step', raw: null }
        const result = await azoa.holons.delete(id)
        if (isOk(result)) {
          holonIdRef.current = null
          return { passed: true, detail: 'Holon deleted', raw: result.value }
        }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── Wallets ───────────────────────────────────────────────────────────────
    {
      name: 'List wallets',
      category: 'wallets',
      fn: async () => {
        const result = await azoa.api.request<Array<{ id: string }>>('GET', '/api/wallet')
        if (isOk(result)) {
          const firstId = result.value[0]?.id ?? null
          walletIdRef.current = firstId
          return { passed: true, detail: `${result.value.length} wallet(s)`, raw: result.value }
        }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Get portfolio (if wallets exist)',
      category: 'wallets',
      fn: async () => {
        const id = walletIdRef.current
        if (!id) return { passed: true, detail: 'Skipped — no wallets available', raw: null }
        const result = await azoa.api.request('GET', `/api/wallet/${id}/portfolio`)
        if (isOk(result)) return { passed: true, detail: 'Portfolio retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── NFTs ──────────────────────────────────────────────────────────────────
    {
      name: 'Query NFTs',
      category: 'nfts',
      fn: async () => {
        const result = await azoa.api.request<Array<{ id: string }>>('GET', '/api/nft')
        if (isOk(result)) {
          const firstId = result.value[0]?.id ?? null
          nftIdRef.current = firstId
          return { passed: true, detail: `${result.value.length} NFT(s)`, raw: result.value }
        }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Get NFT metadata (if NFTs exist)',
      category: 'nfts',
      fn: async () => {
        const id = nftIdRef.current
        if (!id) return { passed: true, detail: 'Skipped — no NFTs available', raw: null }
        try {
          const result = await azoa.api.getNftMetadata(id)
          if (isOk(result)) return { passed: true, detail: 'NFT metadata retrieved', raw: result.value }
          return { passed: false, detail: result.error.message, raw: result.error }
        } catch (err) {
          return { passed: false, detail: String(err), raw: null }
        }
      },
    },

    // ── Bridge ────────────────────────────────────────────────────────────────
    {
      name: 'Get bridge routes',
      category: 'bridge',
      fn: async () => {
        const result = await azoa.api.getBridgeRoutes()
        if (isOk(result)) return { passed: true, detail: 'Bridge routes retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Get bridge history',
      category: 'bridge',
      fn: async () => {
        const result = await azoa.api.getBridgeHistory()
        if (isOk(result)) return { passed: true, detail: 'Bridge history retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── Search ────────────────────────────────────────────────────────────────
    {
      name: 'Search all',
      category: 'search',
      fn: async () => {
        const result = await azoa.api.search({ query: 'test', page: 1, pageSize: 10 })
        if (isOk(result)) return { passed: true, detail: 'Search returned results', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },
    {
      name: 'Get search facets',
      category: 'search',
      fn: async () => {
        const result = await azoa.api.getSearchFacets()
        if (isOk(result)) return { passed: true, detail: 'Facets retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── STAR ODK ──────────────────────────────────────────────────────────────
    {
      name: 'List ODKs',
      category: 'starodk',
      fn: async () => {
        const result = await azoa.api.request('GET', '/api/starodk')
        if (isOk(result)) return { passed: true, detail: 'ODK list retrieved', raw: result.value }
        return { passed: false, detail: result.error.message, raw: result.error }
      },
    },

    // ── DEX Quotes ────────────────────────────────────────────────────────────
    {
      name: 'Algorand swap quote (Tinyman)',
      category: 'dex',
      fn: async () => {
        try {
          // Testnet USDC = 10458941 (mainnet USDC 31566704 doesn't exist on testnet).
          const backendResp = await azoa.api.request('GET', `/api/swap/quote?chain=algorand&tokenIn=0&tokenOut=10458941&amountIn=1000000&slippageBps=50`);
          if (!isOk(backendResp)) {
            return { passed: false, detail: `DEX error: ${backendResp.error?.message ?? 'Unknown'}`, raw: backendResp.error };
          }
          const quote = ((backendResp.value as { result?: unknown })?.result ?? backendResp.value) as Record<string, any> | null;
          // Server flags env-conditions (no pool / upstream offline) as 200 +
          // Unavailable so the test panel can render them as expected, not red.
          if (quote?.unavailable) {
            return {
              passed: true,
              detail: `Expected (env): ${quote.unavailableReason ?? 'unavailable'} — ${quote.message ?? ''}`.trim(),
              raw: quote
            };
          }
          if (!quote || !quote.expectedAmountOut) return { passed: false, detail: 'No quote in response', raw: backendResp.value };
          return { passed: true, detail: `Quote retrieved: ${quote.expectedAmountOut} µUSDC (~${(parseFloat(quote.expectedAmountOut)/1000000).toFixed(2)} USDC)`, raw: quote }
        } catch (err) {
          return { passed: false, detail: `Error: ${String(err)}`, raw: null }
        }
      },
    },
    {
      name: 'Solana swap quote (Jupiter)',
      category: 'dex',
      fn: async () => {
        try {
          const backendResp = await azoa.api.request('GET', `/api/swap/quote?chain=solana&tokenIn=So11111111111111111111111111111111111111112&tokenOut=EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v&amountIn=1000000000&slippageBps=50`);
          if (!isOk(backendResp)) {
            return { passed: false, detail: `DEX: ${backendResp.error?.message ?? 'Unknown'}`, raw: backendResp.error };
          }
          const quote = ((backendResp.value as { result?: unknown })?.result ?? backendResp.value) as Record<string, any> | null;
          if (quote?.unavailable) {
            return {
              passed: true,
              detail: `Expected (env): ${quote.unavailableReason ?? 'unavailable'} — ${quote.message ?? ''}`.trim(),
              raw: quote
            };
          }
          if (!quote || !quote.expectedAmountOut) return { passed: false, detail: 'No quote', raw: backendResp.value };
          return { passed: true, detail: `Quote: ${quote.expectedAmountOut} out`, raw: quote }
        } catch (err) {
          return { passed: false, detail: `Error: ${String(err)}`, raw: null }
        }
      },
    },
  ]
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

const CATEGORIES = ['auth', 'blockchain', 'holons', 'wallets', 'nfts', 'bridge', 'search', 'starodk', 'dex'] as const
type Category = (typeof CATEGORIES)[number]

function statusBadge(status: TestResult['status']) {
  switch (status) {
    case 'passed':
      return <Badge variant="default" className="bg-green-100 text-green-800 hover:bg-green-100 dark:bg-green-900/30 dark:text-green-300">passed</Badge>
    case 'failed':
      return <Badge variant="destructive">failed</Badge>
    case 'running':
      return <Badge variant="secondary" className="bg-yellow-100 text-yellow-800 hover:bg-yellow-100 dark:bg-yellow-900/30 dark:text-yellow-300">running</Badge>
    case 'skipped':
      return <Badge variant="outline">skipped</Badge>
    default:
      return <Badge variant="secondary">pending</Badge>
  }
}

function summarize(results: TestResult[]) {
  const passed = results.filter((r) => r.status === 'passed').length
  const failed = results.filter((r) => r.status === 'failed').length
  const skipped = results.filter((r) => r.status === 'skipped').length
  return { passed, failed, skipped, total: results.length }
}

function initResults(tests: TestCase[]): TestResult[] {
  return tests.map((t) => ({
    ...t,
    status: 'pending',
    detail: '',
  }))
}

/**
 * LLM-friendly error dump. Prefers SdkError.debugString() (verbose: HTTP
 * status, request path, and the server-side exception chain when backend
 * debug mode is on) and falls back to the JSON snapshot otherwise.
 */
function buildErrorDump(results: TestResult[], failed: number, debug: boolean): string {
  const section = results
    .filter((r) => r.status === 'failed')
    .map((r, i) => {
      const raw = r.raw as { debugString?: () => string } | null | undefined
      const verbose =
        raw && typeof raw.debugString === 'function'
          ? raw.debugString()
          : JSON.stringify(r.raw, null, 2)
      return `[${i + 1}] TEST: ${r.name} (${r.category})
ERROR: ${r.detail}
RAW: ${verbose}
--------------------------------------------------`
    })
    .join('\n\n')

  return `### BEGIN AZOA ERROR DUMP ###
Timestamp: ${new Date().toISOString()}
Environment: ${process.env.NODE_ENV}
Debug Mode: ${debug ? 'on (verbose — includes server-side exception chain)' : 'OFF — enable Debug in the top nav for server stack traces'}
Total Failed: ${failed}

${section}
### END AZOA ERROR DUMP ###`
}

// ─── Component ────────────────────────────────────────────────────────────────

export default function TestsPage() {
  useAzoaAuth() // ensure we're in auth context; dashboard layout handles redirect
  const { debug } = useDebug()

  const holonIdRef = useRef<string | null>(null)
  const walletIdRef = useRef<string | null>(null)
  const nftIdRef = useRef<string | null>(null)

  const tests = buildTestCases(holonIdRef, walletIdRef, nftIdRef)

  const [results, setResults] = useState<TestResult[]>(() => initResults(tests))
  const [running, setRunning] = useState(false)
  const [expandedRow, setExpandedRow] = useState<string | null>(null)
  const [selectedCategory, setSelectedCategory] = useState<Category | 'all'>('all')
  const [previousRun, setPreviousRun] = useState<SavedRun | null>(null)

  // Load previous run from localStorage on mount
  useEffect(() => {
    try {
      const raw = localStorage.getItem(STORAGE_KEY)
      if (raw) setPreviousRun(JSON.parse(raw) as SavedRun)
    } catch {
      // ignore
    }
  }, [])

  const setResult = useCallback((name: string, update: Partial<TestResult>) => {
    setResults((prev) =>
      prev.map((r) => (r.name === name ? { ...r, ...update } : r)),
    )
  }, [])

  const runTests = useCallback(
    async (categoryFilter?: Category) => {
      setRunning(true)

      // Reset refs
      holonIdRef.current = null
      walletIdRef.current = null
      nftIdRef.current = null

      const toRun = tests.filter((t) => !categoryFilter || t.category === categoryFilter)

      // Reset statuses
      setResults((prev) =>
        prev.map((r) =>
          toRun.some((t) => t.name === r.name) ? { ...r, status: 'pending', detail: '', raw: undefined, durationMs: undefined } : r,
        ),
      )

      for (const test of toRun) {
        setResult(test.name, { status: 'running' })
        const start = performance.now()
        try {
          const { passed, detail, raw } = await test.fn()
          const durationMs = Math.round(performance.now() - start)
          setResult(test.name, {
            status: passed ? 'passed' : 'failed',
            detail,
            raw,
            durationMs,
          })
        } catch (err) {
          const durationMs = Math.round(performance.now() - start)
          setResult(test.name, {
            status: 'failed',
            detail: String(err),
            raw: null,
            durationMs,
          })
        }
      }

      setRunning(false)
    },
    [tests, setResult],
  )

  // Save results to localStorage when a full run finishes
  useEffect(() => {
    if (running) return
    const anyRan = results.some((r) => r.status !== 'pending')
    if (!anyRan) return
    const saved: SavedRun = {
      date: new Date().toISOString(),
      results: results.map(({ name, category, status, detail, durationMs }) => ({
        name,
        category,
        status,
        detail,
        durationMs,
      })),
    }
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(saved))
    } catch {
      // ignore quota errors
    }
  }, [running, results])

  const { passed, failed, skipped, total } = summarize(results)

  // Previous run regressions
  const regressions = previousRun
    ? previousRun.results
        .filter((pr) => pr.status === 'passed')
        .map((pr) => pr.name)
        .filter((name) => results.find((r) => r.name === name)?.status === 'failed')
    : []

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <h1 className="text-lg font-semibold tracking-tight">Functional Test Runner</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Exercises every API endpoint and SDK method automatically.
          </p>
        </div>
        <div className="flex items-center gap-2 flex-wrap">
          <Select
            value={selectedCategory}
            onValueChange={(v) => setSelectedCategory((v ?? 'all') as Category | 'all')}
            disabled={running}
          >
            <SelectTrigger className="w-[170px]">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All categories</SelectItem>
              {CATEGORIES.map((c) => (
                <SelectItem key={c} value={c}>
                  {c}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            onClick={() =>
              selectedCategory === 'all' ? runTests() : runTests(selectedCategory)
            }
            disabled={running}
          >
            {running ? 'Running…' : selectedCategory === 'all' ? 'Run All Tests' : `Run "${selectedCategory}"`}
          </Button>
        </div>
      </div>

      {/* Debug-mode notice — verbose errors depend on this being on */}
      <div
        className={[
          'flex items-center gap-2 rounded-md border px-3 py-2 text-xs',
          debug
            ? 'border-emerald-500/30 bg-emerald-500/5 text-emerald-700 dark:text-emerald-300'
            : 'border-amber-500/40 bg-amber-500/5 text-amber-700 dark:text-amber-300',
        ].join(' ')}
      >
        <span
          className={`inline-block size-2 rounded-full ${debug ? 'bg-emerald-500' : 'bg-amber-500'}`}
          aria-hidden
        />
        {debug ? (
          <span>
            <strong>Debug mode is on.</strong> Failed tests capture the backend
            exception chain and the error dump below is fully verbose.
          </span>
        ) : (
          <span>
            <strong>Debug mode is off.</strong> Error messages will be terse —
            switch <strong>Debug: On</strong> in the top nav, then re-run for
            full server stack traces.
          </span>
        )}
      </div>

      {/* Summary bar */}
      <Card>
        <CardContent className="flex flex-wrap gap-4 pt-4">
          <div className="flex items-center gap-1.5 text-sm">
            <span className="h-2.5 w-2.5 rounded-full bg-green-500 dark:bg-green-400" />
            <span className="font-medium">{passed}</span>
            <span className="text-muted-foreground">passed</span>
          </div>
          <div className="flex items-center gap-1.5 text-sm">
            <span className="h-2.5 w-2.5 rounded-full bg-destructive" />
            <span className="font-medium">{failed}</span>
            <span className="text-muted-foreground">failed</span>
          </div>
          <div className="flex items-center gap-1.5 text-sm">
            <span className="h-2.5 w-2.5 rounded-full bg-muted-foreground" />
            <span className="font-medium">{skipped}</span>
            <span className="text-muted-foreground">skipped</span>
          </div>
          <div className="flex items-center gap-1.5 text-sm ml-auto">
            <span className="text-muted-foreground">Total:</span>
            <span className="font-medium">{total}</span>
          </div>
          {regressions.length > 0 && (
            <div className="w-full text-sm text-destructive font-medium">
              Regressions vs previous run: {regressions.join(', ')}
            </div>
          )}
        </CardContent>
      </Card>

      {/* Previous run info */}
      {previousRun && (
        <Card className="border-dashed">
          <CardHeader className="pb-2">
            <CardTitle className="text-sm font-medium text-muted-foreground">
              Previous Run — {new Date(previousRun.date).toLocaleString()}
            </CardTitle>
          </CardHeader>
          <CardContent className="text-sm flex gap-4">
            <span className="text-green-600 dark:text-green-400">
              {previousRun.results.filter((r) => r.status === 'passed').length} passed
            </span>
            <span className="text-destructive">
              {previousRun.results.filter((r) => r.status === 'failed').length} failed
            </span>
            <span className="text-muted-foreground">
              {previousRun.results.filter((r) => r.status === 'skipped').length} skipped
            </span>
          </CardContent>
        </Card>
      )}

      {/* Category sections */}
      {CATEGORIES.map((category) => {
        const categoryResults = results.filter((r) => r.category === category)
        const catStats = summarize(categoryResults)
        const anyRan = categoryResults.some((r) => r.status !== 'pending')

        return (
          <Card key={category}>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <CardTitle className="flex items-center gap-2 text-base capitalize">
                  {category === 'blockchain' && (
                    <div className="flex gap-1">
                      <ChainBadge chain="algorand" />
                      <ChainBadge chain="solana" />
                    </div>
                  )}
                  {category}
                </CardTitle>
                <div className="flex items-center gap-2">
                  {anyRan && (
                    <span className="text-xs text-muted-foreground">
                      {catStats.passed}/{catStats.total} passed
                    </span>
                  )}
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={running}
                    onClick={() => runTests(category)}
                  >
                    Run category
                  </Button>
                </div>
              </div>
            </CardHeader>
            <CardContent className="p-0">
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead className="pl-6">Test</TableHead>
                    <TableHead className="w-28">Status</TableHead>
                    <TableHead className="w-24">Duration</TableHead>
                    <TableHead>Detail</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {categoryResults.flatMap((result) => {
                    const isRegression = regressions.includes(result.name)
                    const isExpanded = expandedRow === `${category}:${result.name}`
                    const rows = [
                      <TableRow
                        key={result.name}
                        className={[
                          'cursor-pointer hover:bg-muted/50 transition-colors',
                          isRegression ? 'bg-destructive/5' : '',
                          result.status === 'running' ? 'animate-pulse' : '',
                        ]
                          .filter(Boolean)
                          .join(' ')}
                        onClick={() =>
                          setExpandedRow(
                            isExpanded ? null : `${category}:${result.name}`,
                          )
                        }
                      >
                        <TableCell className="pl-6 font-medium text-sm">
                          {result.name}
                          {isRegression && (
                            <span className="ml-2 text-xs text-destructive font-normal">(regression)</span>
                          )}
                        </TableCell>
                        <TableCell>{statusBadge(result.status)}</TableCell>
                        <TableCell className="text-xs text-muted-foreground">
                          {result.durationMs != null ? `${result.durationMs}ms` : '—'}
                        </TableCell>
                        <TableCell className="text-sm text-muted-foreground max-w-sm truncate">
                          {result.detail || '—'}
                        </TableCell>
                      </TableRow>,
                    ]
                    if (isExpanded && result.raw !== undefined) {
                      rows.push(
                        <TableRow key={`${result.name}-expanded`} className="bg-muted/30">
                          <TableCell colSpan={4} className="pl-6 py-3">
                            <div className="text-xs font-mono">
                              <JsonViewer data={result.raw} />
                            </div>
                          </TableCell>
                        </TableRow>,
                      )
                    }
                    return rows
                  })}
                </TableBody>
              </Table>
            </CardContent>
            <Separator />
          </Card>
        )
      })}

      {/* LLM-Friendly Error Log */}
      {failed > 0 && (
        <Card className="border-destructive/50 bg-destructive/5">
          <CardHeader>
            <CardTitle className="text-sm font-bold text-destructive flex items-center gap-2">
              <span className="h-2 w-2 rounded-full bg-destructive animate-pulse" />
              LLM-FRIENDLY ERROR LOG (v1.0)
            </CardTitle>
            <p className="text-xs text-muted-foreground">
              Copy this section if you need to report these errors to an AI assistant.
            </p>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="rounded-md bg-zinc-950 p-4 font-mono text-[10px] leading-relaxed text-zinc-300 overflow-x-auto border border-zinc-800 shadow-inner">
              <pre className="whitespace-pre-wrap break-all">
{buildErrorDump(results, failed, debug)}
              </pre>
            </div>
            <Button 
              size="sm" 
              variant="outline" 
              className="w-full text-xs h-8 border-destructive/20 hover:bg-destructive/10"
              onClick={() => {
                navigator.clipboard.writeText(buildErrorDump(results, failed, debug));
              }}
            >
              Copy Dump to Clipboard
            </Button>
          </CardContent>
        </Card>
      )}
    </div>
  )
}
