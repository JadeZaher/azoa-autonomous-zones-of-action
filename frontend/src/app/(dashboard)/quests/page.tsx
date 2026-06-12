'use client'

import { useState, useCallback } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ResultDisplay } from '@/components/shared/result-display'
import { ErrorBanner } from '@/components/shared/error-banner'
import { LoadingSkeleton } from '@/components/shared/loading-skeleton'
import { oasis, isOk } from '@/lib/oasis'
import { useOasis } from '@/lib/oasis-context'

// ─── Types ───

interface QuestNode {
  name: string
  nodeType: string
  config: string
  isEntry: boolean
  isTerminal: boolean
  nodeTemplateId?: string
}

interface QuestEdge {
  sourceNodeIndex: number
  targetNodeIndex: number
  condition?: string
  edgeType: string
}

interface Quest {
  id: string
  name: string
  description?: string
  status: string
  nodes: Array<{ id: string; name: string; nodeType: string; state: string; executionOrder: number; isEntry: boolean; isTerminal: boolean; output?: string; error?: string }>
  edges: Array<{ id: string; sourceNodeId: string; targetNodeId: string; edgeType: string; condition?: string }>
  dependencies: unknown[]
  createdDate: string
  completedDate?: string
  metadata: Record<string, string>
}

interface QuestTemplate {
  id: string
  name: string
  description?: string
  version: string
  isPublic: boolean
  tags: string[]
}

// ─── Status colors ───

function statusBadge(status: string) {
  const colors: Record<string, string> = {
    Draft: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-200',
    Active: 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300',
    Completed: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
    Failed: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
    Archived: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300',
    Pending: 'bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300',
    Running: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300',
    Succeeded: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
    Skipped: 'bg-muted text-muted-foreground',
  }
  return <Badge className={colors[status] ?? 'bg-muted text-muted-foreground'}>{status}</Badge>
}

// ─── DAG Visualizer ───

function DagVisualizer({ quest }: { quest: Quest }) {
  const nodeMap = new Map(quest.nodes.map((n) => [n.id, n]))

  return (
    <div className="space-y-3">
      <p className="text-xs text-muted-foreground">Execution order (top to bottom):</p>
      <div className="space-y-2">
        {[...quest.nodes]
          .sort((a, b) => a.executionOrder - b.executionOrder)
          .map((node) => {
            const incomingEdges = quest.edges.filter((e) => e.targetNodeId === node.id)
            const outgoingEdges = quest.edges.filter((e) => e.sourceNodeId === node.id)
            return (
              <div key={node.id} className="flex items-center gap-3">
                <div className="flex h-8 w-8 items-center justify-center rounded-full bg-muted text-xs font-mono">
                  {node.executionOrder}
                </div>
                <div className="flex-1 rounded border p-2">
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium">{node.name}</span>
                    <div className="flex items-center gap-1">
                      {node.isEntry && <Badge variant="outline" className="text-[10px]">Entry</Badge>}
                      {node.isTerminal && <Badge variant="outline" className="text-[10px]">Terminal</Badge>}
                      {statusBadge(node.state)}
                    </div>
                  </div>
                  <p className="text-xs text-muted-foreground">{node.nodeType}</p>
                  {node.output && <p className="mt-1 text-xs text-green-700 truncate">Output: {node.output.slice(0, 80)}...</p>}
                  {node.error && <p className="mt-1 text-xs text-red-600">{node.error}</p>}
                </div>
                <div className="text-xs text-muted-foreground w-20">
                  {incomingEdges.length > 0 && <span>{incomingEdges.length} in</span>}
                  {outgoingEdges.length > 0 && <span className="ml-1">{outgoingEdges.length} out</span>}
                </div>
              </div>
            )
          })}
      </div>
      {quest.edges.length > 0 && (
        <div className="mt-3">
          <p className="text-xs text-muted-foreground mb-1">Edges ({quest.edges.length}):</p>
          <div className="flex flex-wrap gap-1">
            {quest.edges.map((e, i) => {
              const src = nodeMap.get(e.sourceNodeId)
              const tgt = nodeMap.get(e.targetNodeId)
              return (
                <Badge key={i} variant="outline" className="text-[10px]">
                  {src?.name ?? '?'} → {tgt?.name ?? '?'}
                  {e.edgeType === 'Conditional' && ` [${e.condition}]`}
                </Badge>
              )
            })}
          </div>
        </div>
      )}
    </div>
  )
}

// ─── Quest List ───

function QuestList() {
  const { avatarId } = useOasis()
  const [quests, setQuests] = useState<Quest[]>([])
  const [selected, setSelected] = useState<Quest | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [actionResult, setActionResult] = useState<unknown>(null)
  const [actionLoading, setActionLoading] = useState(false)

  const loadQuests = useCallback(async () => {
    if (!avatarId) return
    setLoading(true)
    setError(null)
    try {
      const result = await oasis.api.request<Quest[]>('GET', `/api/quest/avatar/${avatarId}`)
      if (isOk(result)) {
        setQuests(result.value)
      } else {
        setError(result.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }, [avatarId])

  const loadQuest = async (id: string) => {
    const result = await oasis.api.request<Quest>('GET', `/api/quest/${id}`)
    if (isOk(result)) {
      setSelected(result.value)
    }
  }

  const runAction = async (label: string, fn: () => Promise<unknown>) => {
    setActionLoading(true)
    setActionResult(null)
    try {
      const result = await fn() as { ok: boolean; value?: unknown; error?: { message: string } }
      if (result.ok) {
        setActionResult(result.value)
        if (selected) await loadQuest(selected.id) // refresh
      } else {
        setError(result.error?.message ?? `${label} failed`)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setActionLoading(false)
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-2">
        <Button onClick={loadQuests} disabled={loading}>
          {loading ? 'Loading...' : 'Load My Quests'}
        </Button>
        <span className="text-sm text-muted-foreground">{quests.length} quests</span>
      </div>

      {error ? <ErrorBanner message={error} onRetry={loadQuests} /> : null}

      {loading ? <LoadingSkeleton /> : null}

      <div className="grid gap-4 lg:grid-cols-3">
        {/* Quest List */}
        <div className="lg:col-span-1 space-y-2">
          {quests.map((q) => (
            <Card
              key={q.id}
              className={`cursor-pointer transition-colors hover:border-primary ${selected?.id === q.id ? 'border-primary bg-accent/50' : ''}`}
              onClick={() => loadQuest(q.id)}
            >
              <CardContent className="p-3">
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium truncate">{q.name}</span>
                  {statusBadge(q.status)}
                </div>
                <p className="text-xs text-muted-foreground mt-1">
                  {q.nodes?.length ?? 0} nodes, {q.edges?.length ?? 0} edges
                </p>
              </CardContent>
            </Card>
          ))}
          {quests.length === 0 && !loading && (
            <p className="text-sm text-muted-foreground">No quests found. Create one below.</p>
          )}
        </div>

        {/* Quest Detail */}
        <div className="lg:col-span-2">
          {selected !== null ? (
            <Card>
              <CardHeader className="pb-3">
                <div className="flex items-center justify-between">
                  <CardTitle className="text-lg">{selected.name}</CardTitle>
                  {statusBadge(selected.status)}
                </div>
                {selected.description && <p className="text-sm text-muted-foreground">{selected.description}</p>}
              </CardHeader>
              <CardContent className="space-y-4">
                <Tabs defaultValue="dag">
                  <TabsList>
                    <TabsTrigger value="dag">DAG View</TabsTrigger>
                    <TabsTrigger value="actions">Actions</TabsTrigger>
                    <TabsTrigger value="raw">Raw JSON</TabsTrigger>
                  </TabsList>

                  <TabsContent value="dag" className="mt-3">
                    <DagVisualizer quest={selected} />
                  </TabsContent>

                  <TabsContent value="actions" className="mt-3 space-y-3">
                    <div className="flex flex-wrap gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={actionLoading}
                        onClick={() => runAction('Validate', () => oasis.api.request('POST', `/api/quest/${selected.id}/validate`))}
                      >
                        Validate DAG
                      </Button>
                      <Button
                        size="sm"
                        disabled={actionLoading || selected.status === 'Completed'}
                        onClick={() => runAction('Execute', () => oasis.api.request('POST', `/api/quest/${selected.id}/execute`))}
                      >
                        Execute Quest
                      </Button>
                      <Button
                        size="sm"
                        variant="destructive"
                        disabled={actionLoading}
                        onClick={() => runAction('Delete', () => oasis.api.request('DELETE', `/api/quest/${selected.id}`))}
                      >
                        Delete
                      </Button>
                    </div>
                    {actionResult !== null && actionResult !== undefined && (
                      <div>
                        <Separator className="2" />
                        <ResultDisplay result={actionResult} />
                      </div>
                    )}
                  </TabsContent>

                  <TabsContent value="raw" className="mt-3">
                    <JsonViewer data={selected} />
                  </TabsContent>
                </Tabs>
              </CardContent>
            </Card>
          ) : (
            <Card>
              <CardContent className="p-8 text-center text-muted-foreground">
                Select a quest to view its DAG
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  )
}

// ─── Create Quest ───

function CreateQuest({ onCreated }: { onCreated: () => void }) {
  const { avatarId } = useOasis()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [nodesJson, setNodesJson] = useState('[\n  { "name": "Step 1", "nodeType": "HolonCreate", "config": "{\\"name\\":\\"TestHolon\\"}", "isEntry": true, "isTerminal": false },\n  { "name": "Step 2", "nodeType": "HolonGet", "config": "{}", "isEntry": false, "isTerminal": true }\n]')
  const [edgesJson, setEdgesJson] = useState('[{ "sourceNodeIndex": 0, "targetNodeIndex": 1, "edgeType": "Control" }]')
  const [result, setResult] = useState<unknown>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const handleCreate = async () => {
    if (!avatarId || !name) return
    setLoading(true)
    setError(null)
    setResult(null)
    try {
      const nodes = JSON.parse(nodesJson)
      const edges = JSON.parse(edgesJson)
      const res = await oasis.api.request('POST', '/api/quest', { name, description, nodes, edges })
      if (isOk(res)) {
        setResult(res.value)
        onCreated()
      } else {
        setError(res.error.message)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Invalid JSON or request failed')
    } finally {
      setLoading(false)
    }
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">Create Quest DAG</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <Label>Name</Label>
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="My Quest" />
          </div>
          <div className="space-y-1.5">
            <Label>Description</Label>
            <Input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Optional" />
          </div>
        </div>
        <div className="space-y-1.5">
          <Label>Nodes (JSON array)</Label>
          <Textarea
            className="min-h-[100px] font-mono text-xs"
            value={nodesJson}
            onChange={(e) => setNodesJson(e.target.value)}
          />
        </div>
        <div className="space-y-1.5">
          <Label>Edges (JSON array)</Label>
          <Textarea
            className="min-h-[60px] font-mono text-xs"
            value={edgesJson}
            onChange={(e) => setEdgesJson(e.target.value)}
          />
        </div>
        <div className="text-xs text-muted-foreground">
          Node types: HolonCreate, HolonGet, HolonQuery, NftMint, NftTransfer, WalletCreate, StarGenerate, Search, BlockchainExecute, Condition, ComposeOutputs, and 20+ more.
        </div>
        <Button onClick={handleCreate} disabled={loading || !name}>
          {loading ? 'Creating...' : 'Create Quest'}
        </Button>
        {error ? <ErrorBanner message={error} /> : null}
        {result !== null && result !== undefined && <ResultDisplay result={result} />}
      </CardContent>
    </Card>
  )
}

// ─── Templates ───

function TemplateList() {
  const [templates, setTemplates] = useState<QuestTemplate[]>([])
  const [selected, setSelected] = useState<unknown>(null)
  const [loading, setLoading] = useState(false)

  const load = async () => {
    setLoading(true)
    const result = await oasis.api.request<QuestTemplate[]>('GET', '/api/quest/templates')
    if (isOk(result)) setTemplates(result.value)
    setLoading(false)
  }

  const loadDetail = async (id: string) => {
    const result = await oasis.api.request('GET', `/api/quest/templates/${id}`)
    if (isOk(result)) setSelected(result.value)
  }

  return (
    <div className="space-y-3">
      <Button onClick={load} size="sm" disabled={loading}>
        {loading ? 'Loading...' : 'Load Templates'}
      </Button>
      {templates.length > 0 ? (
        <div className="space-y-2">
          {templates.map((t) => (
            <Card key={t.id} className="cursor-pointer hover:border-primary" onClick={() => loadDetail(t.id)}>
              <CardContent className="p-3">
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium">{t.name}</span>
                  <div className="flex gap-1">
                    <Badge variant="outline" className="text-[10px]">v{t.version}</Badge>
                    {t.isPublic && <Badge variant="default" className="text-xs bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300">Public</Badge>}
                  </div>
                </div>
                {t.tags.length > 0 && (
                  <div className="flex gap-1 mt-1">
                    {t.tags.map((tag) => (
                      <Badge key={tag} variant="outline" className="text-[10px]">{tag}</Badge>
                    ))}
                  </div>
                )}
              </CardContent>
            </Card>
          ))}
        </div>
      ) : null}
      {selected !== null && selected !== undefined && (
        <div className="mt-3">
          <Separator />
          <p className="text-xs text-muted-foreground my-2">Template Detail:</p>
          <JsonViewer data={selected} />
        </div>
      )}
    </div>
  )
}

// ─── Node Templates ───

function NodeTemplateList() {
  const [nodeTemplates, setNodeTemplates] = useState<Array<{ id: string; name: string; nodeType: string; version: string }>>([])
  const [loading, setLoading] = useState(false)

  const load = async () => {
    setLoading(true)
    const result = await oasis.api.request<typeof nodeTemplates>('GET', '/api/quest/node-templates')
    if (isOk(result)) setNodeTemplates(result.value)
    setLoading(false)
  }

  return (
    <div className="space-y-3">
      <Button onClick={load} size="sm" disabled={loading}>
        {loading ? 'Loading...' : 'Load Node Templates'}
      </Button>
      {nodeTemplates.length > 0 ? (
        <div className="space-y-1">
          {nodeTemplates.map((nt) => (
            <div key={nt.id} className="flex items-center justify-between rounded border p-2 text-sm">
              <span>{nt.name}</span>
              <div className="flex gap-1">
                <Badge variant="outline" className="text-[10px]">{nt.nodeType}</Badge>
                <Badge variant="outline" className="text-[10px]">v{nt.version}</Badge>
              </div>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  )
}

// ─── Main Page ───

export default function QuestsPage() {
  const [refreshKey, setRefreshKey] = useState(0)

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-lg font-semibold tracking-tight tracking-tight">Quest DAG System</h2>
        <p className="text-muted-foreground">
          Build, validate, and execute directed acyclic graph workflows that orchestrate holons, NFTs, wallets, and blockchain operations.
        </p>
      </div>

      <Tabs defaultValue="quests">
        <TabsList>
          <TabsTrigger value="quests">My Quests</TabsTrigger>
          <TabsTrigger value="create">Create Quest</TabsTrigger>
          <TabsTrigger value="templates">Quest Templates</TabsTrigger>
          <TabsTrigger value="node-templates">Node Templates</TabsTrigger>
        </TabsList>

        <TabsContent value="quests" className="mt-4">
          <QuestList key={refreshKey} />
        </TabsContent>

        <TabsContent value="create" className="mt-4">
          <CreateQuest onCreated={() => setRefreshKey((k) => k + 1)} />
        </TabsContent>

        <TabsContent value="templates" className="mt-4">
          <TemplateList />
        </TabsContent>

        <TabsContent value="node-templates" className="mt-4">
          <NodeTemplateList />
        </TabsContent>
      </Tabs>
    </div>
  )
}
