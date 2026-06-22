'use client'

import { useCallback, useMemo, useRef, useState } from 'react'
import {
  ReactFlow,
  ReactFlowProvider,
  Background,
  Controls,
  MiniMap,
  MarkerType,
  addEdge,
  useNodesState,
  useEdgesState,
  useReactFlow,
  type Connection,
  type Edge,
  type Node,
  type ReactFlowInstance,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { ScrollArea } from '@/components/ui/scroll-area'
import { QuestNode, type QuestNodeData } from './quest-node'
import { layoutGraph } from './layout'
import {
  NODE_CATALOG,
  NODE_CATALOG_BY_TYPE,
  CATEGORY_ORDER,
  CATEGORY_COLOR,
  categoryFor,
  type NodeCategory,
  type NodeTypeMeta,
} from './node-catalog'

const nodeTypes = { quest: QuestNode }

/** A node template fetched from the API, normalized into palette-meta shape. */
export interface NodeTemplateMeta {
  id: string
  name: string
  nodeType: string
  description?: string
  defaultConfig?: string
}

/** Serialized result the parent submits to POST /api/quest (or a template). */
export interface BuiltGraph {
  nodes: Array<{
    name: string
    nodeType: string
    config: string
    isEntry: boolean
    isTerminal: boolean
    nodeTemplateId?: string
  }>
  edges: Array<{
    sourceNodeIndex: number
    targetNodeIndex: number
    edgeType: string
    condition?: string
  }>
}

let idCounter = 0
const nextId = () => `n${++idCounter}_${Math.round(performance.now())}`

interface PaletteEntry extends NodeTypeMeta {
  templateId?: string
  templateName?: string
}

/** Build the palette: catalog entries, augmented/overridden by API node templates. */
function buildPalette(templates: NodeTemplateMeta[]): PaletteEntry[] {
  const entries: PaletteEntry[] = NODE_CATALOG.map((c) => ({ ...c }))
  for (const t of templates) {
    const base = NODE_CATALOG_BY_TYPE[t.nodeType]
    entries.push({
      type: t.nodeType,
      category: base?.category ?? categoryFor(t.nodeType),
      label: t.name,
      description: t.description ?? base?.description ?? `Template for ${t.nodeType}`,
      defaultConfig: t.defaultConfig ?? base?.defaultConfig ?? '{}',
      requiresChain: base?.requiresChain,
      templateId: t.id,
      templateName: t.name,
    })
  }
  return entries
}

interface QuestCanvasProps {
  nodeTemplates: NodeTemplateMeta[]
  /** Called with serialized graph when the user clicks the submit button. */
  onSubmit: (graph: BuiltGraph) => void
  submitting?: boolean
  submitLabel?: string
}

function QuestCanvasInner({ nodeTemplates, onSubmit, submitting, submitLabel = 'Create Quest' }: QuestCanvasProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<QuestNodeData>>([])
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [paletteFilter, setPaletteFilter] = useState('')
  const wrapperRef = useRef<HTMLDivElement>(null)
  const rfInstance = useRef<ReactFlowInstance<Node<QuestNodeData>, Edge> | null>(null)
  const { screenToFlowPosition } = useReactFlow()

  const palette = useMemo(() => buildPalette(nodeTemplates), [nodeTemplates])

  const grouped = useMemo(() => {
    const f = paletteFilter.trim().toLowerCase()
    const map = new Map<NodeCategory, PaletteEntry[]>()
    for (const e of palette) {
      if (f && !`${e.label} ${e.type} ${e.description}`.toLowerCase().includes(f)) continue
      if (!map.has(e.category)) map.set(e.category, [])
      map.get(e.category)!.push(e)
    }
    return map
  }, [palette, paletteFilter])

  const selectedNode = nodes.find((n) => n.id === selectedId) ?? null

  // ─── Add a node ───
  const addNode = useCallback(
    (entry: PaletteEntry, position?: { x: number; y: number }) => {
      const id = nextId()
      const pos = position ?? { x: 120 + nodes.length * 30, y: 80 + nodes.length * 30 }
      const isFirst = nodes.length === 0
      setNodes((nds) =>
        nds.concat({
          id,
          type: 'quest',
          position: pos,
          data: {
            label: entry.templateName ?? entry.label,
            nodeType: entry.type,
            config: entry.defaultConfig,
            isEntry: isFirst,
            isTerminal: false,
            nodeTemplateId: entry.templateId,
          },
        }),
      )
      setSelectedId(id)
    },
    [nodes.length, setNodes],
  )

  // ─── Drag-and-drop from palette ───
  const onDragStart = (e: React.DragEvent, type: string, templateId?: string) => {
    e.dataTransfer.setData('application/quest-node', JSON.stringify({ type, templateId }))
    e.dataTransfer.effectAllowed = 'move'
  }
  const onDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    e.dataTransfer.dropEffect = 'move'
  }, [])
  const onDrop = useCallback(
    (e: React.DragEvent) => {
      e.preventDefault()
      const raw = e.dataTransfer.getData('application/quest-node')
      if (!raw) return
      const { type, templateId } = JSON.parse(raw) as { type: string; templateId?: string }
      const entry =
        palette.find((p) => p.type === type && p.templateId === templateId) ??
        palette.find((p) => p.type === type)
      if (!entry) return
      const position = screenToFlowPosition({ x: e.clientX, y: e.clientY })
      addNode(entry, position)
    },
    [palette, addNode, screenToFlowPosition],
  )

  // ─── Edge wiring ───
  const onConnect = useCallback(
    (c: Connection) => {
      setEdges((eds) =>
        addEdge(
          {
            ...c,
            markerEnd: { type: MarkerType.ArrowClosed },
            style: { stroke: '#94a3b8' },
            data: { edgeType: 'Control' },
          },
          eds,
        ),
      )
    },
    [setEdges],
  )

  // ─── Node config edits ───
  const patchSelected = useCallback(
    (patch: Partial<QuestNodeData>) => {
      if (!selectedId) return
      setNodes((nds) =>
        nds.map((n) => (n.id === selectedId ? { ...n, data: { ...n.data, ...patch } } : n)),
      )
    },
    [selectedId, setNodes],
  )

  const deleteSelected = useCallback(() => {
    if (!selectedId) return
    setNodes((nds) => nds.filter((n) => n.id !== selectedId))
    setEdges((eds) => eds.filter((e) => e.source !== selectedId && e.target !== selectedId))
    setSelectedId(null)
  }, [selectedId, setNodes, setEdges])

  const autoLayout = useCallback(() => {
    setNodes((nds) => layoutGraph(nds, edges) as Node<QuestNodeData>[])
    window.setTimeout(() => rfInstance.current?.fitView({ padding: 0.2 }), 0)
  }, [edges, setNodes])

  const clearAll = useCallback(() => {
    setNodes([])
    setEdges([])
    setSelectedId(null)
  }, [setNodes, setEdges])

  // ─── Serialize and submit ───
  const handleSubmit = useCallback(() => {
    const indexOf = new Map(nodes.map((n, i) => [n.id, i]))
    const graph: BuiltGraph = {
      nodes: nodes.map((n) => ({
        name: n.data.label,
        nodeType: n.data.nodeType,
        config: n.data.config,
        isEntry: n.data.isEntry,
        isTerminal: n.data.isTerminal,
        nodeTemplateId: n.data.nodeTemplateId,
      })),
      edges: edges
        .filter((e) => indexOf.has(e.source) && indexOf.has(e.target))
        .map((e) => ({
          sourceNodeIndex: indexOf.get(e.source)!,
          targetNodeIndex: indexOf.get(e.target)!,
          edgeType: (e.data as { edgeType?: string })?.edgeType ?? 'Control',
          condition: (e.data as { condition?: string })?.condition,
        })),
    }
    onSubmit(graph)
  }, [nodes, edges, onSubmit])

  const configValid = useMemo(() => {
    if (!selectedNode) return true
    try {
      JSON.parse(selectedNode.data.config || '{}')
      return true
    } catch {
      return false
    }
  }, [selectedNode])

  return (
    <div className="flex h-[640px] gap-3">
      {/* ─── Palette ─── */}
      <div className="flex w-56 shrink-0 flex-col rounded-md border bg-card">
        <div className="border-b p-2">
          <Input
            value={paletteFilter}
            onChange={(e) => setPaletteFilter(e.target.value)}
            placeholder="Search nodes…"
            className="h-8 text-xs"
          />
        </div>
        <ScrollArea className="flex-1">
          <div className="space-y-3 p-2">
            {CATEGORY_ORDER.map((cat) => {
              const items = grouped.get(cat)
              if (!items || items.length === 0) return null
              return (
                <div key={cat}>
                  <div className="mb-1 flex items-center gap-1.5">
                    <span className={`h-2 w-2 rounded-full ${CATEGORY_COLOR[cat].dot}`} />
                    <span className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                      {cat}
                    </span>
                  </div>
                  <div className="space-y-1">
                    {items.map((entry) => (
                      <button
                        key={`${entry.type}:${entry.templateId ?? 'builtin'}`}
                        type="button"
                        draggable
                        onDragStart={(e) => onDragStart(e, entry.type, entry.templateId)}
                        onClick={() => addNode(entry)}
                        title={entry.description}
                        className={`flex w-full cursor-grab items-center justify-between rounded border border-l-2 bg-background px-2 py-1 text-left text-xs transition-colors hover:bg-accent active:cursor-grabbing ${CATEGORY_COLOR[cat].border}`}
                      >
                        <span className="truncate">{entry.label}</span>
                        {entry.templateId && (
                          <Badge variant="outline" className="ml-1 shrink-0 text-[8px] px-1 py-0">
                            tpl
                          </Badge>
                        )}
                      </button>
                    ))}
                  </div>
                </div>
              )
            })}
            {grouped.size === 0 && (
              <p className="px-1 text-xs text-muted-foreground">No matching nodes.</p>
            )}
          </div>
        </ScrollArea>
      </div>

      {/* ─── Canvas ─── */}
      <div ref={wrapperRef} className="relative flex-1 overflow-hidden rounded-md border">
        <ReactFlow
          nodes={nodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onConnect={onConnect}
          onInit={(inst) => (rfInstance.current = inst)}
          onDrop={onDrop}
          onDragOver={onDragOver}
          onNodeClick={(_, n) => setSelectedId(n.id)}
          onPaneClick={() => setSelectedId(null)}
          nodeTypes={nodeTypes}
          fitView
          proOptions={{ hideAttribution: true }}
          deleteKeyCode={['Backspace', 'Delete']}
        >
          <Background gap={16} />
          <Controls showInteractive={false} />
          <MiniMap pannable zoomable className="!bg-muted" nodeStrokeWidth={2} />
        </ReactFlow>

        {nodes.length === 0 && (
          <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
            <p className="text-sm text-muted-foreground">
              Click or drag a node from the palette to begin.
            </p>
          </div>
        )}

        {/* Toolbar */}
        <div className="absolute right-2 top-2 z-10 flex gap-1">
          <Button size="sm" variant="outline" onClick={autoLayout} disabled={nodes.length === 0}>
            Auto-layout
          </Button>
          <Button size="sm" variant="outline" onClick={clearAll} disabled={nodes.length === 0}>
            Clear
          </Button>
        </div>
      </div>

      {/* ─── Inspector ─── */}
      <div className="flex w-72 shrink-0 flex-col rounded-md border bg-card">
        <div className="border-b p-2">
          <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            {selectedNode ? 'Node Config' : 'Inspector'}
          </span>
        </div>
        <ScrollArea className="flex-1">
          <div className="space-y-3 p-3">
            {selectedNode ? (
              <>
                <div className="space-y-1.5">
                  <Label className="text-xs">Name</Label>
                  <Input
                    value={selectedNode.data.label}
                    onChange={(e) => patchSelected({ label: e.target.value })}
                    className="h-8 text-xs"
                  />
                </div>
                <div className="space-y-1.5">
                  <Label className="text-xs">Type</Label>
                  <div className="flex items-center gap-1.5">
                    <Badge className={`text-[10px] ${CATEGORY_COLOR[categoryFor(selectedNode.data.nodeType)].badge}`}>
                      {selectedNode.data.nodeType}
                    </Badge>
                    {NODE_CATALOG_BY_TYPE[selectedNode.data.nodeType]?.requiresChain && (
                      <Badge variant="outline" className="text-[9px]">on-chain</Badge>
                    )}
                  </div>
                </div>
                <div className="flex gap-4">
                  <label className="flex items-center gap-1.5 text-xs">
                    <Checkbox
                      checked={selectedNode.data.isEntry}
                      onCheckedChange={(v) => patchSelected({ isEntry: !!v })}
                    />
                    Entry
                  </label>
                  <label className="flex items-center gap-1.5 text-xs">
                    <Checkbox
                      checked={selectedNode.data.isTerminal}
                      onCheckedChange={(v) => patchSelected({ isTerminal: !!v })}
                    />
                    Terminal
                  </label>
                </div>
                <div className="space-y-1.5">
                  <div className="flex items-center justify-between">
                    <Label className="text-xs">Config (JSON)</Label>
                    {!configValid && <span className="text-[10px] text-red-600">invalid JSON</span>}
                  </div>
                  <Textarea
                    value={selectedNode.data.config}
                    onChange={(e) => patchSelected({ config: e.target.value })}
                    className={`min-h-[160px] font-mono text-[11px] ${!configValid ? 'border-red-500' : ''}`}
                  />
                </div>
                <Button size="sm" variant="destructive" className="w-full" onClick={deleteSelected}>
                  Delete Node
                </Button>
              </>
            ) : (
              <p className="text-xs text-muted-foreground">
                Select a node to edit its name, flags, and config. Drag from a node&apos;s bottom
                handle to another node&apos;s top handle to create an edge.
              </p>
            )}
          </div>
        </ScrollArea>

        <div className="space-y-1 border-t p-2 text-[11px] text-muted-foreground">
          <div className="flex justify-between">
            <span>{nodes.length} nodes · {edges.length} edges</span>
          </div>
          <Button
            className="w-full"
            size="sm"
            disabled={submitting || nodes.length === 0}
            onClick={handleSubmit}
          >
            {submitting ? 'Saving…' : submitLabel}
          </Button>
        </div>
      </div>
    </div>
  )
}

export function QuestCanvas(props: QuestCanvasProps) {
  return (
    <ReactFlowProvider>
      <QuestCanvasInner {...props} />
    </ReactFlowProvider>
  )
}
