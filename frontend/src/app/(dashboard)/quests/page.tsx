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
import { RunPanel } from '@/components/quest-builder/run-panel'

// ─── Types ───

interface Quest {
  id: string
  name: string
  description?: string
  status: string
  avatarId: string
  isPublic: boolean
  originAvatarId?: string
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

/** Render a server validation error payload as a readable list. */
function ValidationErrorList({ message }: { message: string }) {
  // The server joins publish-failure validation errors as a semicolon-separated
  // string: "Publish failed — DAG invalid: e1; e2". Attempt JSON.parse first
  // (future-proofing); fall back to splitting on "; " so each error becomes
  // its own bullet. Single-error messages render as a plain paragraph.
  let lines: string[] = []
  try {
    const parsed = JSON.parse(message)
    if (Array.isArray(parsed)) lines = parsed.map(String)
  } catch {
    // Not JSON — check for semicolon-joined publish failure format.
    const PUBLISH_PREFIX = 'Publish failed — DAG invalid: '
    if (message.includes('; ')) {
      const body = message.startsWith(PUBLISH_PREFIX)
        ? message.slice(PUBLISH_PREFIX.length)
        : message
      lines = body.split('; ').map((s) => s.trim()).filter(Boolean)
    } else {
      lines = [message]
    }
  }
  if (lines.length === 1) return <p className="text-sm text-red-600">{lines[0]}</p>
  return (
    <ul className="space-y-0.5 rounded border border-red-200 bg-red-50 p-2 text-xs text-red-700 dark:bg-red-900/20 dark:text-red-400">
      {lines.map((l, i) => (
        <li key={i} className="flex gap-1"><span aria-hidden>✕</span><span>{l}</span></li>
      ))}
    </ul>
  )
}

function QuestList() {
  const { avatarId } = useAzoa()
  const [quests, setQuests] = useState<Quest[]>([])
  const [selected, setSelected] = useState<Quest | null>(null)
  const [loading, setLoading] = useState(false)
  const [listError, setListError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [actionResult, setActionResult] = useState<unknown>(null)
  const [actionLoading, setActionLoading] = useState(false)
  const [detailTab, setDetailTab] = useState<'dag' | 'runs' | 'actions' | 'raw'>('dag')

  const loadQuests = useCallback(async () => {
    if (!avatarId) return
    setLoading(true)
    setListError(null)
    try {
      const result = await azoa.api.request<Quest[]>('GET', `/api/quest/avatar/${avatarId}`)
      if (isOk(result)) setQuests(result.value)
      else setListError(result.error.message)
    } catch (e) {
      setListError(e instanceof Error ? e.message : 'Unknown error')
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
    setActionError(null)
    try {
      const result = (await fn()) as { ok: boolean; value?: unknown; error?: { message: string } }
      if (result.ok) {
        setActionResult(result.value)
        if (selected) await loadQuest(selected.id)
        // Reload list to reflect status changes (e.g. publish flips Draft→Active).
        await loadQuests()
      } else {
        setActionError(result.error?.message ?? `${label} failed`)
      }
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Unknown error')
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

      {listError ? <ErrorBanner message={listError} onRetry={loadQuests} /> : null}
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
                        { id: 'runs', label: 'Runs' },
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

                    {detailTab === 'runs' && (
                      <RunPanel questId={selected.id} quest={selected} />
                    )}

                    {detailTab === 'actions' && (
                      <div className="flex flex-col gap-3">
                        {/* ─── Marketplace visibility ─── */}
                        <label className="flex items-center gap-2 text-sm">
                          <Checkbox
                            checked={selected.isPublic}
                            disabled={actionLoading}
                            onCheckedChange={(v) =>
                              runAction('Update visibility', () =>
                                azoa.api.request('PUT', `/api/quest/${selected.id}`, { isPublic: !!v }),
                              )
                            }
                          />
                          Publish to marketplace (let other avatars start this quest)
                        </label>
                        <Separator />
                        {/* ─── Lifecycle actions (G1) ─── */}
                        <div className="flex flex-wrap gap-2">
                          {selected.status === 'Draft' && (
                            <Button
                              size="sm"
                              variant="default"
                              disabled={actionLoading}
                              onClick={() => runAction('Publish', () => azoa.api.request('POST', `/api/quest/${selected.id}/publish`))}
                            >
                              Publish
                            </Button>
                          )}
                          {selected.status === 'Active' && (
                            <Button
                              size="sm"
                              variant="outline"
                              disabled={actionLoading}
                              onClick={() => runAction('Unpublish', () => azoa.api.request('POST', `/api/quest/${selected.id}/unpublish`))}
                            >
                              Unpublish
                            </Button>
                          )}
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
                            variant="destructive"
                            disabled={actionLoading}
                            onClick={() => runAction('Delete', () => azoa.api.request('DELETE', `/api/quest/${selected.id}`))}
                          >
                            Delete
                          </Button>
                        </div>
                        {selected.status === 'Draft' && (
                          <p className="text-xs text-muted-foreground">
                            This quest is a <strong>Draft</strong> — publish it to enable execution.
                          </p>
                        )}
                        {selected.status === 'Active' && (
                          <p className="text-xs text-muted-foreground">
                            This quest is <strong>Active</strong> — unpublish it to edit nodes or edges. Start a durable run from the <strong>Runs</strong> tab.
                          </p>
                        )}
                        {/* Render server validation errors as a readable list (G1) */}
                        {actionError && <ValidationErrorList message={actionError} />}
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
  const [isPublic, setIsPublic] = useState(false)
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
          isPublic,
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
    [name, description, isPublic, onCreated],
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

      <label className="flex items-center gap-2 text-sm">
        <Checkbox checked={isPublic} onCheckedChange={(v) => setIsPublic(!!v)} />
        Publish to marketplace (other avatars can start this quest once it's Active)
      </label>

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

// ─── Start a Public Quest (non-owner) ───

/**
 * Marketplace browse + start. Fetches the public quest catalog on mount
 * (`azoa.api.listPublicQuests()` → GET /api/quest/public) and renders it as a
 * selectable card grid; clicking a card loads it into the start flow. A manual
 * id-paste remains as a fallback. Mirrors the backend's `LoadStartableQuestAsync`
 * marketplace rule: owner-or-(public && Active).
 */
function StartPublicQuest() {
  const { avatarId } = useAzoa()
  const [questId, setQuestId] = useState('')
  const [quest, setQuest] = useState<Quest | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [starting, setStarting] = useState(false)
  const [startError, setStartError] = useState<string | null>(null)
  const [startResult, setStartResult] = useState<unknown>(null)

  const [catalog, setCatalog] = useState<Quest[]>([])
  const [catalogLoading, setCatalogLoading] = useState(true)
  const [catalogError, setCatalogError] = useState<string | null>(null)

  const loadCatalog = useCallback(async () => {
    setCatalogLoading(true)
    setCatalogError(null)
    const result = await azoa.api.listPublicQuests()
    if (isOk(result)) setCatalog(result.value as unknown as Quest[])
    else setCatalogError(result.error.message)
    setCatalogLoading(false)
  }, [])

  useEffect(() => {
    loadCatalog()
  }, [loadCatalog])

  const handleLoad = async () => {
    if (!questId.trim()) return
    setLoading(true)
    setLoadError(null)
    setQuest(null)
    setStartResult(null)
    setStartError(null)
    const result = await azoa.api.request<Quest>('GET', `/api/quest/${questId.trim()}`)
    if (isOk(result)) setQuest(result.value)
    else setLoadError(result.error.message)
    setLoading(false)
  }

  /** Select a quest from the browse grid — loads it straight into the start flow. */
  const handleSelect = (q: Quest) => {
    setQuestId(q.id)
    setQuest(q)
    setLoadError(null)
    setStartResult(null)
    setStartError(null)
  }

  const handleStart = async () => {
    if (!quest) return
    setStarting(true)
    setStartError(null)
    setStartResult(null)
    const result = await azoa.api.request('POST', `/api/quest/${quest.id}/execute`)
    if (isOk(result)) setStartResult(result.value)
    else setStartError(result.error.message)
    setStarting(false)
  }

  const isOwner = quest != null && quest.avatarId === avatarId

  return (
    <div className="flex flex-col gap-4 rounded-lg border bg-card p-4">
      <div>
        <h3 className="text-sm font-semibold">Public Quest Marketplace</h3>
        <p className="text-sm text-muted-foreground">
          Browse quests published to the marketplace and start one under your own avatar.
        </p>
      </div>

      {/* Browse grid */}
      {catalogLoading ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }).map((_, i) => (
            <Card key={i} className="animate-pulse">
              <CardContent className="h-24 pt-6" />
            </Card>
          ))}
        </div>
      ) : catalogError ? (
        <ErrorBanner message={catalogError} onRetry={loadCatalog} />
      ) : catalog.length === 0 ? (
        <p className="text-sm text-muted-foreground">No public quests published yet.</p>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {catalog.map((q) => (
            <button
              key={q.id}
              type="button"
              onClick={() => handleSelect(q)}
              className={`flex flex-col gap-2 rounded-md border p-3 text-left transition-colors hover:border-primary/60 hover:bg-accent ${
                quest?.id === q.id ? 'border-primary bg-accent' : ''
              }`}
            >
              <div className="flex items-start justify-between gap-2">
                <p className="text-sm font-medium">{q.name}</p>
                {statusBadge(q.status)}
              </div>
              {q.description && (
                <p className="line-clamp-2 text-xs text-muted-foreground">{q.description}</p>
              )}
              {q.originAvatarId && (
                <p className="text-[10px] text-muted-foreground">
                  by <code className="font-mono">{q.originAvatarId.slice(0, 8)}…</code>
                </p>
              )}
            </button>
          ))}
        </div>
      )}

      <Separator />

      {/* Manual id-paste fallback */}
      <div className="flex flex-col gap-1.5 sm:max-w-md">
        <Label htmlFor="public-quest-id">Or paste a Quest ID</Label>
        <div className="flex gap-2">
          <Input
            id="public-quest-id"
            value={questId}
            onChange={(e) => setQuestId(e.target.value)}
            placeholder="e.g. 3fa85f64-5717-4562-b3fc-2c963f66afa6"
          />
          <Button onClick={handleLoad} disabled={loading || !questId.trim()}>
            {loading ? 'Loading...' : 'Load'}
          </Button>
        </div>
      </div>

      {loadError && <ErrorBanner message={loadError} onRetry={handleLoad} />}

      {quest && (
        <div className="flex flex-col gap-3 rounded-md border p-3">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm font-medium">{quest.name}</p>
              {quest.description && <p className="text-xs text-muted-foreground">{quest.description}</p>}
            </div>
            {statusBadge(quest.status)}
          </div>

          {isOwner ? (
            <p className="text-xs text-muted-foreground">
              You own this quest — start it from the <strong>My Quests</strong> tab instead.
            </p>
          ) : quest.isPublic && quest.status === 'Active' ? (
            <Button size="sm" disabled={starting} onClick={handleStart}>
              {starting ? 'Starting...' : 'Start this quest'}
            </Button>
          ) : (
            <p className="text-xs text-muted-foreground">
              This quest isn't published for execution yet.
            </p>
          )}

          {startError && <p className="text-sm text-destructive">{startError}</p>}
          {startResult !== null && startResult !== undefined && (
            <div>
              <Separator />
              <ResultDisplay result={startResult} />
            </div>
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
    { id: 'start-public', label: 'Start a Quest' },
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
        {tab === 'start-public' && <StartPublicQuest />}
        {tab === 'templates' && <QuestTemplates />}
        {tab === 'node-templates' && <NodeTemplates />}
      </div>
    </div>
  )
}
