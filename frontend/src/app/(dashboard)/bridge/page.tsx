'use client'

import { useState } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ResultDisplay } from '@/components/shared/result-display'
import { ChainBadge } from '@/components/shared/chain-badge'
import { azoa, isOk } from '@/lib/azoa'

// ─── Types ───

interface BridgeRoute {
  sourceChain: string
  targetChain: string
  supportedAssetTypes: string[]
  wormholeSupported: boolean
  availableModes: string[]
  [key: string]: unknown
}

interface BridgeTransaction {
  id?: string
  status?: string
  [key: string]: unknown
}

interface BridgeStatus {
  id: string
  status: string
  bridgeMode?: string
  sourceChain?: string
  targetChain?: string
  amount?: number
  lockTxHash?: string
  mintTxHash?: string
  vaaBytes?: string
  errorMessage?: string
}

interface BridgeHistoryItem {
  id: string
  sourceChain: string
  targetChain: string
  amount: number | string
  status: string
  createdAt: string
}

const BRIDGE_STEPS = ['Initiated', 'Locked', 'AwaitingVAA', 'VAAReady', 'Completed']

function stepIndex(status: string) {
  return BRIDGE_STEPS.findIndex(
    (s) => s.toLowerCase() === status?.toLowerCase()
  )
}

// ─── Route Explorer ───

function RouteExplorer() {
  const [routes, setRoutes] = useState<BridgeRoute[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadRoutes = async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.getBridgeRoutes()
      if (isOk(result)) {
        setRoutes(result.value as unknown as BridgeRoute[])
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between pb-3">
        <CardTitle className="text-sm">Route Explorer</CardTitle>
        <Button size="sm" onClick={loadRoutes} disabled={loading}>
          {loading ? 'Loading...' : 'Load Routes'}
        </Button>
      </CardHeader>
      <CardContent>
        {error && <p className="text-sm text-destructive mb-3">{error}</p>}
        {routes.length === 0 && !loading && (
          <p className="text-sm text-muted-foreground">No routes loaded yet.</p>
        )}
        {routes.length > 0 && (
          <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Source</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Asset Types</TableHead>
                <TableHead>Wormhole</TableHead>
                <TableHead>Modes</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {routes.map((route, i) => (
                <TableRow key={i}>
                  <TableCell>
                    <ChainBadge chain={route.sourceChain} />
                  </TableCell>
                  <TableCell>
                    <ChainBadge chain={route.targetChain} />
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      {(route.supportedAssetTypes ?? []).map((t) => (
                        <Badge key={t} variant="outline" className="text-xs">
                          {t}
                        </Badge>
                      ))}
                    </div>
                  </TableCell>
                  <TableCell>
                    <Badge
                      variant={route.wormholeSupported ? 'default' : 'secondary'}
                      className="text-xs"
                    >
                      {route.wormholeSupported ? 'Yes' : 'No'}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      {(route.availableModes ?? []).map((m) => (
                        <Badge key={m} variant="secondary" className="text-xs">
                          {m}
                        </Badge>
                      ))}
                    </div>
                  </TableCell>
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

// ─── Initiate Bridge Form ───

function InitiateBridgeForm() {
  const [form, setForm] = useState({
    sourceChain: '',
    targetChain: '',
    tokenId: '',
    recipientAddress: '',
    amount: '',
    mode: '',
  })
  const [result, setResult] = useState<BridgeTransaction | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const set = (field: string, value: string) =>
    setForm((prev) => ({ ...prev, [field]: value }))

  const handleSubmit = async () => {
    setLoading(true)
    setError(null)
    setResult(null)
    try {
      const res = await azoa.api.initiateBridge({
        sourceChain: form.sourceChain,
        targetChain: form.targetChain,
        tokenId: form.tokenId,
        recipientAddress: form.recipientAddress,
        amount: Number(form.amount),
        mode: form.mode as "Trusted" | "Wormhole",
      })
      if (isOk(res)) {
        setResult(res.value as unknown as BridgeTransaction)
      } else {
        setError((res as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">Initiate Bridge</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label>Source Chain</Label>
            <Select value={form.sourceChain} onValueChange={(v) => set('sourceChain', v ?? '')}>
              <SelectTrigger>
                <SelectValue placeholder="Select chain" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Algorand">Algorand</SelectItem>
                <SelectItem value="Solana">Solana</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1.5">
            <Label>Target Chain</Label>
            <Select value={form.targetChain} onValueChange={(v) => set('targetChain', v ?? '')}>
              <SelectTrigger>
                <SelectValue placeholder="Select chain" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Algorand">Algorand</SelectItem>
                <SelectItem value="Solana">Solana</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label>Token ID</Label>
            <Input
              placeholder="Asset/mint identifier"
              value={form.tokenId}
              onChange={(e) => set('tokenId', e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Amount</Label>
            <Input
              type="number"
              placeholder="Amount (integer)"
              value={form.amount}
              onChange={(e) => set('amount', e.target.value)}
            />
          </div>
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label>Recipient Address</Label>
            <Input
              placeholder="Recipient wallet address"
              value={form.recipientAddress}
              onChange={(e) => set('recipientAddress', e.target.value)}
            />
          </div>
          <div className="space-y-1.5">
            <Label>Bridge Mode</Label>
            <Select value={form.mode} onValueChange={(v) => set('mode', v ?? 'Trusted')}>
              <SelectTrigger>
                <SelectValue placeholder="Select mode" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Trusted">Trusted</SelectItem>
                <SelectItem value="Wormhole">Wormhole</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <Button
          onClick={handleSubmit}
          disabled={
            loading ||
            !form.sourceChain ||
            !form.targetChain ||
            !form.tokenId ||
            !form.amount ||
            !form.mode
          }
        >
          {loading ? 'Initiating...' : 'Initiate Bridge'}
        </Button>

        {error && <ResultDisplay isError message={error} />}
        {result && <ResultDisplay result={result} message="Bridge initiated successfully" />}
      </CardContent>
    </Card>
  )
}

// ─── Bridge Status Tracker ───

function BridgeStatusTracker() {
  const [bridgeId, setBridgeId] = useState('')
  const [status, setStatus] = useState<BridgeStatus | null>(null)
  const [vaa, setVaa] = useState<unknown>(null)
  const [actionResult, setActionResult] = useState<unknown>(null)
  const [loading, setLoading] = useState(false)
  const [actionLoading, setActionLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const checkStatus = async () => {
    if (!bridgeId) return
    setLoading(true)
    setError(null)
    setStatus(null)
    try {
      const result = await azoa.api.getBridgeStatus(bridgeId)
      if (isOk(result)) {
        setStatus(result.value as unknown as BridgeStatus)
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  const runAction = async (
    label: string,
    fn: () => Promise<unknown>
  ) => {
    setActionLoading(true)
    setError(null)
    setActionResult(null)
    try {
      const result = await fn() as { ok: boolean; value?: unknown; error?: { message: string } }
      if (result.ok) {
        if (label === 'vaa') setVaa(result.value)
        else setActionResult(result.value)
      } else {
        setError(result.error?.message ?? 'Unknown error')
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setActionLoading(false)
    }
  }

  const currentStep = status ? stepIndex(status.status) : -1
  const isWormhole = String(status?.bridgeMode ?? '').toLowerCase() === 'wormhole'

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">Bridge Status Tracker</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex gap-2">
          <Input
            placeholder="Bridge transaction ID"
            value={bridgeId}
            onChange={(e) => setBridgeId(e.target.value)}
            className="flex-1"
          />
          <Button onClick={checkStatus} disabled={loading || !bridgeId}>
            {loading ? 'Checking...' : 'Check Status'}
          </Button>
        </div>

        {error && <p className="text-sm text-destructive">{error}</p>}

        {status !== null && (
          <div className="space-y-4">
            {/* Step Indicator */}
            <div className="space-y-1">
              <p className="text-xs text-muted-foreground mb-2">Progress</p>
              <div className="flex items-center gap-0">
                {BRIDGE_STEPS.map((step, i) => (
                  <div key={step} className="flex items-center">
                    <div
                      className={`flex h-8 min-w-[90px] items-center justify-center rounded px-2 text-xs font-medium transition-colors ${
                        i < currentStep
                          ? 'bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300'
                          : i === currentStep
                            ? 'bg-blue-600 text-white dark:bg-blue-500'
                            : 'bg-muted text-muted-foreground'
                      }`}
                    >
                      {step}
                    </div>
                    {i < BRIDGE_STEPS.length - 1 && (
                      <div
                        className={`h-0.5 w-4 ${
                          i < currentStep ? 'bg-green-400 dark:bg-green-500' : 'bg-border'
                        }`}
                      />
                    )}
                  </div>
                ))}
              </div>
            </div>

            <Separator />

            {/* Actions */}
            <div className="flex flex-wrap gap-2">
              {isWormhole && (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={actionLoading}
                  onClick={() =>
                    runAction('vaa', () => azoa.api.fetchVAA(bridgeId))
                  }
                >
                  Fetch VAA
                </Button>
              )}
              <Button
                size="sm"
                variant="outline"
                disabled={actionLoading}
                onClick={() =>
                  runAction('redeem', () => azoa.api.redeemBridge(bridgeId))
                }
              >
                Redeem
              </Button>
              {!isWormhole && (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={actionLoading}
                  onClick={() =>
                    runAction('complete', () => azoa.api.completeBridge(bridgeId))
                  }
                >
                  Complete
                </Button>
              )}
            </div>

            {/* Raw status */}
            <div className="rounded-md bg-muted p-3 text-xs">
              <JsonViewer data={status} />
            </div>

            {/* VAA */}
            {vaa !== null && (
              <div className="space-y-1">
                <p className="text-xs text-muted-foreground">VAA Data</p>
                <div className="rounded-md bg-muted p-3 text-xs">
                  <JsonViewer data={vaa} />
                </div>
              </div>
            )}

            {/* Action Result */}
            {actionResult !== null && <ResultDisplay result={actionResult} />}
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── Bridge History ───

function BridgeHistory() {
  const [history, setHistory] = useState<BridgeHistoryItem[]>([])
  const [selected, setSelected] = useState<BridgeHistoryItem | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadHistory = async () => {
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.getBridgeHistory()
      if (isOk(result)) {
        setHistory(result.value as BridgeHistoryItem[])
      } else {
        setError((result as { error: { message: string } }).error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }

  const statusColor = (s: string) => {
    const lower = s?.toLowerCase()
    if (lower === 'completed') return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300'
    if (lower === 'failed') return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300'
    return 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300'
  }

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between pb-3">
        <CardTitle className="text-sm">Bridge History</CardTitle>
        <Button size="sm" onClick={loadHistory} disabled={loading}>
          {loading ? 'Loading...' : 'Load History'}
        </Button>
      </CardHeader>
      <CardContent className="space-y-4">
        {error && <p className="text-sm text-destructive">{error}</p>}
        {history.length === 0 && !loading && (
          <p className="text-sm text-muted-foreground">No history loaded.</p>
        )}
        {history.length > 0 && (
          <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>ID</TableHead>
                <TableHead>Source</TableHead>
                <TableHead>Target</TableHead>
                <TableHead>Amount</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Created</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {history.map((item) => (
                <TableRow
                  key={item.id}
                  className="cursor-pointer"
                  onClick={() => setSelected(selected?.id === item.id ? null : item)}
                >
                  <TableCell className="font-mono text-xs">
                    {item.id.length > 12 ? `${item.id.slice(0, 8)}…` : item.id}
                  </TableCell>
                  <TableCell>
                    <ChainBadge chain={item.sourceChain} />
                  </TableCell>
                  <TableCell>
                    <ChainBadge chain={item.targetChain} />
                  </TableCell>
                  <TableCell className="font-mono">{item.amount}</TableCell>
                  <TableCell>
                    <Badge className={`text-xs ${statusColor(item.status)}`}>
                      {item.status}
                    </Badge>
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {new Date(item.createdAt).toLocaleDateString()}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          </div>
        )}

        {selected && (
          <div className="space-y-1">
            <p className="text-xs text-muted-foreground">Details — {selected.id}</p>
            <div className="rounded-md bg-muted p-3 text-xs">
              <JsonViewer data={selected} />
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// ─── Page ───

export default function BridgePage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-lg font-semibold tracking-tight tracking-tight">Bridge</h1>
        <p className="text-sm text-muted-foreground">
          Cross-chain asset bridging via Trusted or Wormhole modes
        </p>
      </div>

      <RouteExplorer />
      <InitiateBridgeForm />
      <BridgeStatusTracker />
      <BridgeHistory />
    </div>
  )
}
