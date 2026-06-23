'use client'

import { useState, useCallback, useEffect } from 'react'
import { ChevronRight } from 'lucide-react'
import { Card, CardContent } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { Separator } from '@/components/ui/separator'
import { JsonViewer } from '@/components/shared/json-viewer'
import { ResultDisplay } from '@/components/shared/result-display'
import { ErrorBanner } from '@/components/shared/error-banner'
import { LoadingSkeleton } from '@/components/shared/loading-skeleton'
import { azoa, isOk } from '@/lib/azoa'
import { useAzoa } from '@/lib/azoa-context'
import { DagFlow } from '@/components/quest-builder/dag-flow'
import { QuestCanvas, type BuiltGraph, type NodeTemplateMeta } from '@/components/quest-builder/quest-canvas'
import { NodeTemplateCreator } from '@/components/quest-builder/node-template-creator'

// ─── Types ───

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
  }
  return <Badge className={colors[status] ?? 'bg-muted text-muted-foreground'}>{status}</Badge>
}

/** Fetch node templates and normalize them into palette-meta shape. */
function useNodeTemplates() {
  const [templates, setTemplates] = useState<NodeTemplateMeta[]>([])

  const reload = useCallback(async () => {
    const res = await azoa.api.request<Array<{ id: string; name: string; nodeType: string; description?: string; defaultConfig?: string }>>(
      'GET',
      '/api/quest/node-templates',
    )
    if (isOk(res)) {
      setTemplates(
        res.value.map((t) => ({
          id: t.id,
          name: t.name,
          nodeType: t.nodeType,
          description: t.description,
          defaultConfig: t.defaultConfig,
        })),
      )
    }
  }, [])

  useEffect(() => {
    reload()
  }, [reload])

  return { templates, reload }
}

// ─── My Quests ───

function QuestList() {
  const { avatarId } = useAzoa()
  const [quests, setQuests] = useState<Quest[]>([])
  const [selected, setSelected] = useState<Quest | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [actionResult, setActionResult] = useState<unknown>(null)
  const [actionLoading, setActionLoading] = useState(false)
  const [detailTab, setDetailTab] = useState<'dag' | 'actions' | 'raw'>('dag')

  const loadQuests = useCallback(async () => {
    if (!avatarId) return
    setLoading(true)
    setError(null)
    try {
      const result = await azoa.api.request<Quest[]>('GET', `/api/quest/avatar/${avatarId}`)
      if (isOk(result)) setQuests(result.value)
      else setError(result.error.message)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setLoading(false)
    }
  }, [avatarId])

  const loadQuest = async (id: string) => {
    const result = await azoa.api.request<Quest>('GET', `/api/quest/${id}`)
    if (isOk(result)) setSelected(result.value)
  }

  const runAction = async (label: string, fn: () => Promise<unknown>) => {
    setActionLoading(true)
    setActionResult(null)
    try {
      const result = (await fn()) as { ok: boolean; value?: unknown; error?: { message: string } }
      if (result.ok) {
        setActionResult(result.value)
        if (selected) await loadQuest(selected.id)
      } else {
        setError(result.error?.message ?? `${label} failed`)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setActionLoading(false)
    }
  }

  const toggle = (q: Quest) => {
    if (selected?.id === q.id) {
      setSelected(null)
    } else {
      void loadQuest(q.id)
    }
  }

  return (
    <div className="space-y-3">
      {/* Action row */}
      <div className="flex flex-wrap items-center gap-2">
        <Button onClick={loadQuests} disabled={loading}>
          {loading ? 'Loading...' : 'Load My Quests'}
        </Button>
        <span className="text-sm text-muted-foreground">{quests.length} quests</span>
      </div>

      {error ? <ErrorBanner message={error} onRetry={loadQuests} /> : null}
      {loading ? <LoadingSkeleton /> : null}

      {/* Full-width list with inline-expanding detail */}
      <div className="divide-y rounded-md border">
        {quests.map((q) => {
          const open = selected?.id === q.id
          return (
            <div key={q.id}>
              <button
                type="button"
                onClick={() => toggle(q)}
                className={`flex w-full items-center gap-3 px-3 py-2.5 text-left transition-colors hover:bg-accent/50 ${open ? 'bg-accent/40' : ''}`}
              >
                <ChevronRight className={`h-4 w-4 shrink-0 text-muted-foreground transition-transform ${open ? 'rotate-90' : ''}`} />
                <span className="flex-1 truncate text-sm font-medium">{q.name}</span>
                <span className="hidden text-xs text-muted-foreground sm:inline">
                  {q.nodes?.length ?? 0} nodes · {q.edges?.length ?? 0} edges
                </span>
                {statusBadge(q.status)}
              </button>

              {open && selected && (
                <div className="border-t bg-background px-4 py-4">
                  {selected.description && (
                    <p className="mb-3 text-sm text-muted-foreground">{selected.description}</p>
                  )}
                  <div className="flex flex-col gap-3">
                    {/* Detail tab switcher — plain buttons */}
                    <div className="flex gap-2 border-b pb-px">
                      {([
                        { id: 'dag', label: 'DAG View' },
                        { id: 'actions', label: 'Actions' },
                        { id: 'raw', label: 'Raw JSON' },
                      ] as const).map((t) => (
                        <button
                          key={t.id}
                          type="button"
                          onClick={() => setDetailTab(t.id)}
                          className={`-mb-px border-b-2 px-2.5 py-1.5 text-xs font-medium transition-colors ${
                            detailTab === t.id
                              ? 'border-primary text-foreground'
                              : 'border-transparent text-muted-foreground hover:text-foreground'
                          }`}
                        >
                          {t.label}
                        </button>
                      ))}
                    </div>

                    {detailTab === 'dag' && (
                      <DagFlow nodes={selected.nodes} edges={selected.edges} />
                    )}

                    {detailTab === 'actions' && (
                      <div className="flex flex-col gap-3">
                        <div className="flex flex-wrap gap-2">
                          <Button
                            size="sm"
                            variant="outline"
                            disabled={actionLoading}
                            onClick={() => runAction('Validate', () => azoa.api.request('POST', `/api/quest/${selected.id}/validate`))}
                          >
                            Validate DAG
                          </Button>
                          <Button
                            size="sm"
                            disabled={actionLoading || selected.status === 'Completed'}
                            onClick={() => runAction('Execute', () => azoa.api.request('POST', `/api/quest/${selected.id}/execute`))}
                          >
                            Execute Quest
                          </Button>
                          <Button
                            size="sm"
                            variant="destructive"
                            disabled={actionLoading}
                            onClick={() => runAction('Delete', () => azoa.api.request('DELETE', `/api/quest/${selected.id}`))}
                          >
                            Delete
                          </Button>
                        </div>
                        {actionResult !== null && actionResult !== undefined && (
                          <div>
                            <Separator />
                            <ResultDisplay result={actionResult} />
                          </div>
                        )}
                      </div>
                    )}

                    {detailTab === 'raw' && <JsonViewer data={selected} />}
                  </div>
                </div>
              )}
            </div>
          )
        })}
        {quests.length === 0 && !loading && (
          <p className="px-3 py-6 text-center text-sm text-muted-foreground">
            No quests found. Build one in the Builder tab.
          </p>
        )}
      </div>
    </div>
  )
}

// ─── Quest Builder (visual) ───

function QuestBuilder({ onCreated }: { onCreated: () => void }) {
  const { templates, reload } = useNodeTemplates()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [result, setResult] = useState<unknown>(null)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = useCallback(
    async (graph: BuiltGraph) => {
      if (!name.trim()) {
        setError('Quest name is required')
        return
      }
      setSubmitting(true)
      setError(null)
      setResult(null)
      try {
        const res = await azoa.api.request('POST', '/api/quest', {
          name: name.trim(),
          description: description.trim() || undefined,
          nodes: graph.nodes,
          edges: graph.edges,
        })
        if (isOk(res)) {
          setResult(res.value)
          onCreated()
        } else {
          setError(res.error.message)
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Request failed')
      } finally {
        setSubmitting(false)
      }
    },
    [name, description, onCreated],
  )

  return (
    <div className="flex flex-col gap-4 rounded-lg border bg-card p-4">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold">Visual Quest Builder</h3>
        <Button size="sm" variant="ghost" onClick={reload}>
          Refresh palette
        </Button>
      </div>

      <div className="flex flex-col gap-4 sm:flex-row">
        <div className="flex flex-1 flex-col gap-1.5">
          <Label>Quest Name</Label>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="My Quest" />
        </div>
        <div className="flex flex-1 flex-col gap-1.5">
          <Label>Description</Label>
          <Input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Optional" />
        </div>
      </div>

      <QuestCanvas nodeTemplates={templates} onSubmit={handleSubmit} submitting={submitting} submitLabel="Create Quest" />

      {error ? <ErrorBanner message={error} /> : null}
      {result !== null && result !== undefined && <ResultDisplay result={result} />}
    </div>
  )
}

// ─── Quest Templates ───

function QuestTemplates() {
  const { templates: nodeTemplates } = useNodeTemplates()
  const [templates, setTemplates] = useState<QuestTemplate[]>([])
  const [selected, setSelected] = useState<unknown>(null)
  const [loading, setLoading] = useState(false)
  const [mode, setMode] = useState<'list' | 'create'>('list')

  const load = useCallback(async () => {
    setLoading(true)
    const result = await azoa.api.request<QuestTemplate[]>('GET', '/api/quest/templates')
    if (isOk(result)) setTemplates(result.value)
    setLoading(false)
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const loadDetail = async (id: string) => {
    const result = await azoa.api.request('GET', `/api/quest/templates/${id}`)
    if (isOk(result)) setSelected(result.value)
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <Button size="sm" variant={mode === 'list' ? 'default' : 'outline'} onClick={() => setMode('list')}>
          Browse
        </Button>
        <Button size="sm" variant={mode === 'create' ? 'default' : 'outline'} onClick={() => setMode('create')}>
          Create Template
        </Button>
      </div>

      {mode === 'create' ? (
        <QuestTemplateCreator nodeTemplates={nodeTemplates} onCreated={() => { setMode('list'); load() }} />
      ) : (
        <div className="space-y-3">
          <Button onClick={load} size="sm" variant="outline" disabled={loading}>
            {loading ? 'Loading...' : 'Reload'}
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
                        {t.isPublic && (
                          <Badge className="bg-green-100 text-xs text-green-800 dark:bg-green-900/30 dark:text-green-300">Public</Badge>
                        )}
                      </div>
                    </div>
                    {t.tags.length > 0 && (
                      <div className="mt-1 flex gap-1">
                        {t.tags.map((tag) => (
                          <Badge key={tag} variant="outline" className="text-[10px]">{tag}</Badge>
                        ))}
                      </div>
                    )}
                  </CardContent>
                </Card>
              ))}
            </div>
          ) : (
            !loading && <p className="text-sm text-muted-foreground">No templates yet. Create one.</p>
          )}
          {selected !== null && selected !== undefined && (
            <div className="mt-3">
              <Separator />
              <p className="my-2 text-xs text-muted-foreground">Template Detail:</p>
              <JsonViewer data={selected} />
            </div>
          )}
        </div>
      )}
    </div>
  )
}

/** Quest Template creator — same visual canvas, posts to /api/quest/templates. */
function QuestTemplateCreator({ nodeTemplates, onCreated }: { nodeTemplates: NodeTemplateMeta[]; onCreated: () => void }) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [version, setVersion] = useState('1.0.0')
  const [tags, setTags] = useState('')
  const [isPublic, setIsPublic] = useState(false)
  const [result, setResult] = useState<unknown>(null)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = useCallback(
    async (graph: BuiltGraph) => {
      if (!name.trim()) {
        setError('Template name is required')
        return
      }
      setSubmitting(true)
      setError(null)
      setResult(null)
      try {
        const res = await azoa.api.request('POST', '/api/quest/templates', {
          name: name.trim(),
          description: description.trim() || undefined,
          nodes: graph.nodes,
          edges: graph.edges,
          version: version.trim() || '1.0.0',
          isPublic,
          tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
        })
        if (isOk(res)) {
          setResult(res.value)
          onCreated()
        } else {
          setError(res.error.message)
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Request failed')
      } finally {
        setSubmitting(false)
      }
    },
    [name, description, version, tags, isPublic, onCreated],
  )

  return (
    <div className="space-y-3">
      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label>Template Name</Label>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="Onboarding Flow" />
        </div>
        <div className="space-y-1.5">
          <Label>Description</Label>
          <Input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Optional" />
        </div>
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="space-y-1.5">
          <Label>Version</Label>
          <Input value={version} onChange={(e) => setVersion(e.target.value)} placeholder="1.0.0" />
        </div>
        <div className="space-y-1.5 sm:col-span-2">
          <Label>Tags (comma-separated)</Label>
          <Input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="onboarding, demo" />
        </div>
      </div>
      <label className="flex items-center gap-2 text-sm">
        <Checkbox checked={isPublic} onCheckedChange={(v) => setIsPublic(!!v)} />
        Public (shareable with other avatars)
      </label>

      <QuestCanvas nodeTemplates={nodeTemplates} onSubmit={handleSubmit} submitting={submitting} submitLabel="Create Template" />

      {error ? <ErrorBanner message={error} /> : null}
      {result !== null && result !== undefined && <ResultDisplay result={result} />}
    </div>
  )
}

// ─── Node Templates ───

function NodeTemplates() {
  const [nodeTemplates, setNodeTemplates] = useState<Array<{ id: string; name: string; nodeType: string; version: string; description?: string; isPublic?: boolean; tags?: string[] }>>([])
  const [loading, setLoading] = useState(false)
  const [mode, setMode] = useState<'list' | 'create'>('list')

  const load = useCallback(async () => {
    setLoading(true)
    const result = await azoa.api.request<typeof nodeTemplates>('GET', '/api/quest/node-templates')
    if (isOk(result)) setNodeTemplates(result.value)
    setLoading(false)
  }, [])

  useEffect(() => {
    load()
  }, [load])

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <Button size="sm" variant={mode === 'list' ? 'default' : 'outline'} onClick={() => setMode('list')}>
          Browse
        </Button>
        <Button size="sm" variant={mode === 'create' ? 'default' : 'outline'} onClick={() => setMode('create')}>
          Create Node Template
        </Button>
      </div>

      {mode === 'create' ? (
        <NodeTemplateCreator onCreated={() => { setMode('list'); load() }} />
      ) : (
        <div className="space-y-3">
          <Button onClick={load} size="sm" variant="outline" disabled={loading}>
            {loading ? 'Loading...' : 'Reload'}
          </Button>
          {nodeTemplates.length > 0 ? (
            <div className="space-y-1">
              {nodeTemplates.map((nt) => (
                <div key={nt.id} className="flex items-center justify-between rounded border p-2 text-sm">
                  <div>
                    <span className="font-medium">{nt.name}</span>
                    {nt.description && <span className="ml-2 text-xs text-muted-foreground">{nt.description}</span>}
                  </div>
                  <div className="flex gap-1">
                    <Badge variant="outline" className="text-[10px]">{nt.nodeType}</Badge>
                    <Badge variant="outline" className="text-[10px]">v{nt.version}</Badge>
                    {nt.isPublic && (
                      <Badge className="bg-green-100 text-[10px] text-green-800 dark:bg-green-900/30 dark:text-green-300">Public</Badge>
                    )}
                  </div>
                </div>
              ))}
            </div>
          ) : (
            !loading && <p className="text-sm text-muted-foreground">No node templates yet. Create one to seed the builder palette.</p>
          )}
        </div>
      )}
    </div>
  )
}

// ─── Main Page ───

export default function QuestsPage() {
  const [refreshKey, setRefreshKey] = useState(0)
  const [tab, setTab] = useState('quests')

  const onQuestCreated = () => {
    setRefreshKey((k) => k + 1)
    setTab('quests')
  }

  const tabs = [
    { id: 'quests', label: 'My Quests' },
    { id: 'create', label: 'Builder' },
    { id: 'templates', label: 'Quest Templates' },
    { id: 'node-templates', label: 'Node Templates' },
  ] as const

  return (
    <div className="flex flex-col gap-6">
      {/* Top bar — header stack */}
      <div className="flex flex-col gap-1">
        <h2 className="text-lg font-semibold tracking-tight">Quest DAG System</h2>
        <p className="text-sm text-muted-foreground">
          Build, validate, and execute directed acyclic graph workflows that orchestrate holons, NFTs, wallets, and blockchain operations.
        </p>
      </div>

      {/* Tab switcher — plain buttons, no primitive */}
      <div className="flex flex-wrap gap-2 border-b pb-px">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            onClick={() => setTab(t.id)}
            className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
              tab === t.id
                ? 'border-primary text-foreground'
                : 'border-transparent text-muted-foreground hover:text-foreground'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {/* Panel — only the active one renders */}
      <div>
        {tab === 'quests' && <QuestList key={refreshKey} />}
        {tab === 'create' && <QuestBuilder onCreated={onQuestCreated} />}
        {tab === 'templates' && <QuestTemplates />}
        {tab === 'node-templates' && <NodeTemplates />}
      </div>
    </div>
  )
}
