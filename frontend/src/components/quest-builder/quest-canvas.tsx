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
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuItem,
  DropdownMenuSeparator,
} from '@/components/ui/dropdown-menu'
import { QuestNode, type QuestNodeData } from './quest-node'
import { layoutGraph } from './layout'
import { QUEST_PRESETS, presetIsLoadable, type QuestPreset } from './presets'
import {
  NODE_CATALOG,
  NODE_CATALOG_BY_TYPE,
  CATEGORY_ORDER,
  CATEGORY_COLOR,
  categoryFor,
  type NodeCategory,
  type NodeTypeMeta,
} from './node-catalog'
import { getOutputShapeMirror } from './node-output-schema'

/** Data stored on each edge in the canvas. */
interface EdgeData {
  // Mirrors backend QuestEdgeType (Control | Conditional | OnFailure). OnFailure is
  // a first-class error-routing edge; it is NOT a Control edge (see dominator/fan-out
  // logic below, which keys on edgeType === 'Control' exactly).
  edgeType?: 'Control' | 'Conditional' | 'OnFailure'
  condition?: string
}

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
    // NOTE: these are array indices into `nodes`, not Guids. The backend
    // QuestEdgeCreateModel binds them by these exact names (SourceNodeId /
    // TargetNodeId, camelCased over the wire), so the key names must match
    // or every edge silently binds 0→0 and fails self-loop validation.
    sourceNodeId: number
    targetNodeId: number
    edgeType: string
    condition?: string
  }>
}

let idCounter = 0
const nextId = () => `n${++idCounter}_${Math.round(performance.now())}`

/** Visual style for an edge based on its type. */
function edgeStyle(type: string | undefined): Pick<Edge, 'animated' | 'style' | 'label'> {
  if (type === 'Conditional') {
    return { animated: true, style: { stroke: '#f59e0b', strokeDasharray: '5 5' } }
  }
  if (type === 'OnFailure') {
    return { animated: false, style: { stroke: '#ef4444', strokeDasharray: '4 4' } }
  }
  return { animated: false, style: { stroke: '#94a3b8' } }
}

interface PaletteEntry extends NodeTypeMeta {
  templateId?: string
  templateName?: string
}

// ─── $from binding-site collection (mirrors QuestDagExecutabilityValidator.CollectBindingSites) ───
// Walks a node's config JSON for `{"$from": "<path>"}` objects at property-value
// positions only; array-element occurrences are skipped (the backend rejects
// those separately at config-validation time, not here).
function collectBindingSites(configJson: string | undefined): Array<{ path: string }> {
  const sites: Array<{ path: string }> = []
  if (!configJson || !configJson.includes('"$from"')) return sites
  let root: unknown
  try {
    root = JSON.parse(configJson)
  } catch {
    return sites
  }
  walkForBindings(root, sites)
  return sites
}

function walkForBindings(node: unknown, sites: Array<{ path: string }>): void {
  if (Array.isArray(node)) {
    for (const elem of node) walkForBindings(elem, sites)
    return
  }
  if (node && typeof node === 'object') {
    const obj = node as Record<string, unknown>
    const keys = Object.keys(obj)
    for (const key of keys) {
      const child = obj[key]
      if (
        child &&
        typeof child === 'object' &&
        !Array.isArray(child) &&
        Object.keys(child as Record<string, unknown>).length === 1 &&
        typeof (child as Record<string, unknown>).$from === 'string'
      ) {
        sites.push({ path: (child as Record<string, unknown>).$from as string })
      } else {
        walkForBindings(child, sites)
      }
    }
  }
}

// ─── Dominator dataflow (mirrors QuestDagExecutabilityValidator.ComputeDominators) ───
// Computes, per node id, the set of GUARANTEED ancestors (dominators, excluding
// self): nodes present on EVERY path from an entry to it. Reuses the same
// forward-dataflow fixpoint as the backend, over the canvas's `adj`/reverse-adj.
function computeDominators(nodeIds: string[], preds: Map<string, string[]>): Map<string, Set<string>> {
  const allIds = new Set(nodeIds)
  const entryIds = new Set(nodeIds.filter((id) => (preds.get(id) ?? []).length === 0))

  const dom = new Map<string, Set<string>>()
  for (const id of nodeIds) {
    dom.set(id, entryIds.has(id) ? new Set([id]) : new Set(allIds))
  }

  let changed = true
  let guard = 0
  while (changed && guard++ <= nodeIds.length + 2) {
    changed = false
    for (const id of nodeIds) {
      if (entryIds.has(id)) continue
      let intersection: Set<string> | null = null
      for (const p of preds.get(id) ?? []) {
        const domP = dom.get(p)
        if (!domP) continue
        if (intersection === null) {
          intersection = new Set(domP)
        } else {
          for (const x of Array.from(intersection)) {
            if (!domP.has(x)) intersection.delete(x)
          }
        }
      }
      intersection ??= new Set()
      intersection.add(id)

      const cur = dom.get(id)!
      const same = cur.size === intersection.size && Array.from(cur).every((x) => intersection!.has(x))
      if (!same) {
        dom.set(id, intersection)
        changed = true
      }
    }
  }

  for (const id of nodeIds) dom.get(id)!.delete(id)
  return dom
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
  /** When true, node/edge mutations are disabled (Active quest). */
  readOnly?: boolean
}

function QuestCanvasInner({ nodeTemplates, onSubmit, submitting, submitLabel = 'Create Quest', readOnly = false }: QuestCanvasProps) {
  const [nodes, setNodes, onNodesChange] = useNodesState<Node<QuestNodeData>>([])
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null)
  const [paletteFilter, setPaletteFilter] = useState('')
  const wrapperRef = useRef<HTMLDivElement>(null)
  const rfInstance = useRef<ReactFlowInstance<Node<QuestNodeData>, Edge> | null>(null)
  const { screenToFlowPosition, setCenter } = useReactFlow()

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
  const selectedEdge = edges.find((e) => e.id === selectedEdgeId) ?? null

  // ─── Patch an edge's data (EdgeType / condition) ───
  const patchEdge = useCallback(
    (patch: Partial<EdgeData>) => {
      if (!selectedEdgeId) return
      setEdges((eds) =>
        eds.map((e) =>
          e.id === selectedEdgeId
            ? { ...e, data: { ...(e.data as EdgeData), ...patch }, ...(edgeStyle(patch.edgeType ?? (e.data as EdgeData)?.edgeType ?? 'Control')) }
            : e,
        ),
      )
    },
    [selectedEdgeId, setEdges],
  )

  // ─── Add a node ───
  const addNode = useCallback(
    (entry: PaletteEntry, position?: { x: number; y: number }) => {
      const id = nextId()
      const pos = position ?? { x: 120 + nodes.length * 30, y: 80 + nodes.length * 30 }
      const isFirst = nodes.length === 0
      // Auto-uniquify the label: node names are the binding key ($from "upstream.<name>"),
      // and the server rejects duplicate names at publish. Append " 2"/" 3"/… on collision.
      const baseLabel = entry.templateName ?? entry.label
      const existing = new Set(nodes.map((n) => n.data.label))
      let label = baseLabel
      let suffix = 2
      while (existing.has(label)) label = `${baseLabel} ${suffix++}`
      setNodes((nds) =>
        nds.concat({
          id,
          type: 'quest',
          position: pos,
          data: {
            label,
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
            ...edgeStyle('Control'),
            data: { edgeType: 'Control' } satisfies EdgeData,
          },
          eds,
        ),
      )
    },
    [setEdges],
  )

  const onEdgeClick = useCallback((_: React.MouseEvent, edge: Edge) => {
    setSelectedEdgeId(edge.id)
    setSelectedId(null)
  }, [])

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

  const deleteSelectedEdge = useCallback(() => {
    if (!selectedEdgeId) return
    setEdges((eds) => eds.filter((e) => e.id !== selectedEdgeId))
    setSelectedEdgeId(null)
  }, [selectedEdgeId, setEdges])

  const autoLayout = useCallback(() => {
    setNodes((nds) => layoutGraph(nds, edges) as Node<QuestNodeData>[])
    window.setTimeout(() => rfInstance.current?.fitView({ padding: 0.2 }), 0)
  }, [edges, setNodes])

  const clearAll = useCallback(() => {
    setNodes([])
    setEdges([])
    setSelectedId(null)
  }, [setNodes, setEdges])

  // ─── Load a preset graph ───
  const loadPreset = useCallback(
    (preset: QuestPreset) => {
      // Map preset node keys → freshly minted canvas ids so edges can reference them.
      const idByKey = new Map<string, string>()
      const rawNodes: Node<QuestNodeData>[] = preset.nodes.map((pn) => {
        const id = nextId()
        idByKey.set(pn.key, id)
        const catalog = NODE_CATALOG_BY_TYPE[pn.nodeType]
        return {
          id,
          type: 'quest',
          position: { x: 0, y: 0 }, // replaced by layoutGraph below
          data: {
            label: pn.label ?? catalog?.label ?? pn.nodeType,
            nodeType: pn.nodeType,
            config: pn.config ?? catalog?.defaultConfig ?? '{}',
            isEntry: !!pn.isEntry,
            isTerminal: !!pn.isTerminal,
          },
        }
      })

      const rawEdges: Edge[] = preset.edges
        .filter((pe) => idByKey.has(pe.from) && idByKey.has(pe.to))
        .map((pe) => ({
          id: `e_${idByKey.get(pe.from)}_${idByKey.get(pe.to)}`,
          source: idByKey.get(pe.from)!,
          target: idByKey.get(pe.to)!,
          markerEnd: { type: MarkerType.ArrowClosed },
          ...edgeStyle(pe.edgeType),
          data: { edgeType: (pe.edgeType as EdgeData['edgeType']) ?? 'Control', condition: pe.condition } satisfies EdgeData,
        }))

      setNodes(layoutGraph(rawNodes, rawEdges) as Node<QuestNodeData>[])
      setEdges(rawEdges)
      setSelectedId(null)
      window.setTimeout(() => rfInstance.current?.fitView({ padding: 0.2 }), 0)
    },
    [setNodes, setEdges],
  )

  // Conditional edge with empty condition is a server hard-reject (FR-1c / FR-8b).
  const conditionalEdgeMissingCondition = useMemo(() =>
    edges.filter((e) => (e.data as EdgeData)?.edgeType === 'Conditional' && !((e.data as EdgeData)?.condition ?? '').trim()),
  [edges])

  // Any node with invalid JSON config blocks submit (G3 — config validity pre-submit).
  const nodesWithInvalidConfig = useMemo(() =>
    nodes.filter((n) => {
      try { JSON.parse(n.data.config || '{}'); return false } catch { return true }
    }),
  [nodes])

  // ─── Serialize and submit ───
  // NOTE: the two useMemos above MUST stay before this callback — its dependency
  // array references them, and a const in the temporal dead zone throws
  // ReferenceError on every render if declared later.
  const handleSubmit = useCallback(() => {
    // Hard blocks: invalid config JSON or Conditional edges missing condition
    // will be rejected by the server anyway — surface them here first.
    if (nodesWithInvalidConfig.length > 0) return
    if (conditionalEdgeMissingCondition.length > 0) return

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
          sourceNodeId: indexOf.get(e.source)!,
          targetNodeId: indexOf.get(e.target)!,
          edgeType: (e.data as EdgeData)?.edgeType ?? 'Control',
          condition: (e.data as EdgeData)?.condition,
        })),
    }
    onSubmit(graph)
  }, [nodes, edges, onSubmit, nodesWithInvalidConfig, conditionalEdgeMissingCondition])

  const configValid = useMemo(() => {
    if (!selectedNode) return true
    try {
      JSON.parse(selectedNode.data.config || '{}')
      return true
    } catch {
      return false
    }
  }, [selectedNode])

  // ─── Client-side DAG pre-check ───
  // Mirrors the backend QuestDagValidator rules so the user sees structural
  // problems (missing entry/terminal, orphans, cycles) BEFORE submitting and
  // eating a 400. Not authoritative — the server re-validates — but it stops
  // the common "why won't my quest save" guessing game.
  //
  // Warning levels: { text, error: true } = blocks publish/submit on the server.
  const dagWarnings = useMemo(() => {
    // nodeIds: the canvas node ids this warning is "about", used to (a) paint an
    // authoring-warning ring on the offending node and (b) click-to-center from the list.
    const warnings: Array<{ text: string; error?: boolean; nodeIds?: string[] }> = []
    if (nodes.length === 0) return warnings

    const ids = new Set(nodes.map((n) => n.id))
    const incoming = new Map(nodes.map((n) => [n.id, 0]))
    const outgoing = new Map(nodes.map((n) => [n.id, 0]))
    const controlOut = new Map(nodes.map((n) => [n.id, 0]))
    const adj = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
    for (const e of edges) {
      if (!ids.has(e.source) || !ids.has(e.target)) continue
      incoming.set(e.target, (incoming.get(e.target) ?? 0) + 1)
      outgoing.set(e.source, (outgoing.get(e.source) ?? 0) + 1)
      // Fan-out / dominators key on Control ONLY — mirrors the backend validator
      // (QuestDagValidator fan-out + QuestDagExecutabilityValidator dominators use
      // EdgeType==Control exclusively). OnFailure and Conditional are excluded.
      if ((e.data as EdgeData)?.edgeType === 'Control') {
        controlOut.set(e.source, (controlOut.get(e.source) ?? 0) + 1)
      }
      adj.get(e.source)!.push(e.target)
    }

    const roots = nodes.filter((n) => (incoming.get(n.id) ?? 0) === 0)
    const markedEntries = nodes.filter((n) => n.data.isEntry)
    const markedTerminals = nodes.filter((n) => n.data.isTerminal)

    if (markedEntries.length === 0) {
      warnings.push({ text: `No node is marked as Entry. Mark the starting node's "Entry" flag in the inspector.` })
    }
    const unmarkedRoots = roots.filter((n) => !n.data.isEntry)
    if (unmarkedRoots.length > 0) {
      warnings.push({
        text: `Orphan (no incoming edge, not Entry): ${unmarkedRoots.map((n) => n.data.label).join(', ')}.`,
        nodeIds: unmarkedRoots.map((n) => n.id),
      })
    }
    if (markedTerminals.length === 0) {
      warnings.push({ text: `No node is marked as Terminal. Mark a leaf node's "Terminal" flag in the inspector.` })
    }
    const terminalNotLeaf = markedTerminals.filter((n) => (outgoing.get(n.id) ?? 0) > 0)
    if (terminalNotLeaf.length > 0) {
      warnings.push({
        text: `Marked Terminal but has outgoing edges: ${terminalNotLeaf.map((n) => n.data.label).join(', ')}.`,
        nodeIds: terminalNotLeaf.map((n) => n.id),
      })
    }

    // Fan-out: >1 outgoing Control edge from any node. Durable engine rejects at
    // publish; shown as error-level (won't publish) rather than advisory.
    const fanOutNodes = nodes.filter((n) => (controlOut.get(n.id) ?? 0) > 1)
    for (const n of fanOutNodes) {
      warnings.push({
        text: `Fan-out on "${n.data.label}" (${controlOut.get(n.id)} outgoing Control edges) — won't publish; durable engine requires a single Control successor.`,
        error: true,
        nodeIds: [n.id],
      })
    }

    // Conditional edges missing condition text: server hard-rejects these (FR-1c).
    for (const e of conditionalEdgeMissingCondition) {
      const src = nodes.find((n) => n.id === e.source)
      const tgt = nodes.find((n) => n.id === e.target)
      warnings.push({
        text: `Conditional edge from "${src?.data.label ?? e.source}" → "${tgt?.data.label ?? e.target}" has no condition text — server will reject this edge.`,
        error: true,
        nodeIds: [e.source, e.target].filter((id) => ids.has(id)),
      })
    }

    // Nodes with invalid config JSON block the server round-trip (FR-8d).
    for (const n of nodesWithInvalidConfig) {
      warnings.push({
        text: `Node "${n.data.label}" has invalid JSON config — fix before submitting.`,
        error: true,
        nodeIds: [n.id],
      })
    }

    // ── $from binding executability (mirrors QuestDagExecutabilityValidator) ──
    // Best-effort + non-authoritative: the server is the real gate. When unsure
    // (open schema, holon.*, deep paths, malformed config) we SKIP rather than
    // risk a false positive — nodesWithInvalidConfig already flags bad JSON.
    {
      const invalidConfigIds = new Set(nodesWithInvalidConfig.map((n) => n.id))
      const nameById = new Map(nodes.map((n) => [n.id, n.data.label]))
      const idsByName = new Map<string, string[]>()
      for (const n of nodes) {
        const list = idsByName.get(n.data.label) ?? []
        list.push(n.id)
        idsByName.set(n.data.label, list)
      }

      // Duplicate labels: server rejects at publish, and the binding checker below
      // silently skips ambiguous names — so surface it explicitly (error-level).
      // forEach (not for..of) — Map iteration needs downlevelIteration under this tsconfig.
      idsByName.forEach((dupIds, name) => {
        if (dupIds.length > 1) {
          warnings.push({
            text: `Duplicate node name "${name}" (×${dupIds.length}) — bindings are ambiguous and publish will fail. Rename so every node is unique.`,
            error: true,
            nodeIds: dupIds,
          })
        }
      })
      // ALL-edge predecessors: upstream-scope mirror (BuildUpstreamScope walks EVERY
      // incoming edge regardless of type). directPredNames uses this — intentional.
      const preds = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
      adj.forEach((targets, src) => {
        for (const tgt of targets) preds.get(tgt)?.push(src)
      })
      // Control-ONLY predecessors: dominators are computed over the Control spine only
      // (mirrors backend QuestDagExecutabilityValidator.ComputeDominators, which
      // traverses EdgeType==Control exclusively). Conditional/OnFailure edges do NOT
      // contribute a guaranteed-ancestry path.
      const controlPreds = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
      for (const e of edges) {
        if (!ids.has(e.source) || !ids.has(e.target)) continue
        if ((e.data as EdgeData)?.edgeType !== 'Control') continue
        controlPreds.get(e.target)?.push(e.source)
      }
      const dominators = computeDominators(nodes.map((n) => n.id), controlPreds)

      for (const consumer of nodes) {
        if (invalidConfigIds.has(consumer.id)) continue // already flagged; don't double-report
        const sites = collectBindingSites(consumer.data.config)
        if (sites.length === 0) continue

        const directPredNames = new Set(
          (preds.get(consumer.id) ?? []).map((id) => nameById.get(id)).filter((x): x is string => !!x),
        )
        const domNames = new Set(
          Array.from(dominators.get(consumer.id) ?? []).map((id) => nameById.get(id)).filter((x): x is string => !!x),
        )

        for (const { path } of sites) {
          const segments = path.split('.')
          if (segments.length < 2) continue // malformed — grammar errors are the config validator's job
          const [root, refName, ...rest] = segments

          if (root === 'holon') continue // dynamic state — skip

          if (root !== 'upstream' && root !== 'run') {
            warnings.push({
              text: `Node "${consumer.data.label}": "${path}" has an unrecognized binding root "${root}" (expected "upstream", "run", or "holon").`,
              error: true,
              nodeIds: [consumer.id],
            })
            continue
          }

          // Ambiguous name (duplicate labels) — skip rather than risk a false positive.
          const candidateIds = idsByName.get(refName)
          if (!candidateIds || candidateIds.length === 0) continue // unresolvable name — skip (server will catch it)
          if (candidateIds.length > 1) continue

          const refNode = nodes.find((n) => n.id === candidateIds[0])
          if (!refNode) continue

          if (root === 'upstream') {
            if (!directPredNames.has(refName)) {
              warnings.push({
                text: `Node "${consumer.data.label}": upstream binding "${path}" references "${refName}" which is not a direct predecessor.`,
                error: true,
                nodeIds: [consumer.id],
              })
              continue
            }
          } else {
            // root === 'run'
            if (!domNames.has(refName)) {
              warnings.push({
                text: `Node "${consumer.data.label}": run binding "${path}" references "${refName}", which is not guaranteed to run before it (not on every path) — its output may be absent.`,
                error: true,
                nodeIds: [consumer.id],
              })
              continue
            }
          }

          // ── Field presence (best-effort; skip on open schema or ambiguity) ──
          const shape = getOutputShapeMirror(refNode.data.nodeType)
          if (shape.open) continue // opaque output — ancestry alone gates it

          if (rest.length === 0) continue // no field segment to check — nothing more to say

          const fieldSeg = rest[0]
          const matchedKey = Object.keys(shape.fields).find(
            (f) => f.toLowerCase() === fieldSeg.toLowerCase(),
          )
          if (!matchedKey) {
            warnings.push({
              text: `Node "${consumer.data.label}": "${path}" — field "${fieldSeg}" is not in the output of "${refName}".`,
              error: true,
              nodeIds: [consumer.id],
            })
            continue
          }
          // Deep paths into a "deep" (Object/Array) field are admitted with no
          // further checks (unknown nested shape); scalar fields with extra
          // segments are also skipped client-side (never false-positive).
        }
      }
    }

    // Reachability + cycle detection via BFS from marked entries.
    const reachable = new Set<string>()
    const queue = markedEntries.map((n) => n.id)
    queue.forEach((id) => reachable.add(id))
    while (queue.length > 0) {
      const cur = queue.shift()!
      for (const next of adj.get(cur) ?? []) {
        if (!reachable.has(next)) {
          reachable.add(next)
          queue.push(next)
        }
      }
    }
    if (markedEntries.length > 0) {
      const unreachable = nodes.filter((n) => !reachable.has(n.id))
      if (unreachable.length > 0) {
        warnings.push({
          text: `Not reachable from an Entry node: ${unreachable.map((n) => n.data.label).join(', ')}.`,
          nodeIds: unreachable.map((n) => n.id),
        })
      }
    }

    // Kahn cycle detection.
    const indeg = new Map(nodes.map((n) => [n.id, incoming.get(n.id) ?? 0]))
    const kq = nodes.filter((n) => (indeg.get(n.id) ?? 0) === 0).map((n) => n.id)
    let visited = 0
    while (kq.length > 0) {
      const cur = kq.shift()!
      visited++
      for (const next of adj.get(cur) ?? []) {
        indeg.set(next, (indeg.get(next) ?? 0) - 1)
        if ((indeg.get(next) ?? 0) === 0) kq.push(next)
      }
    }
    if (visited !== nodes.length) {
      warnings.push({ text: 'Graph contains a cycle — quests must be acyclic (DAG).' })
    }

    // Skip-cascade advisory: remind authors that a failed/skipped node cascades
    // through the entire downstream Control chain, not just one hop.
    if (edges.some((e) => (e.data as EdgeData)?.edgeType === 'Control')) {
      warnings.push({
        text: 'Skip cascades: if any node fails or is skipped, ALL downstream Control-chain nodes are also skipped (not just the next hop).',
      })
    }

    return warnings
  }, [nodes, edges, conditionalEdgeMissingCondition, nodesWithInvalidConfig])

  // Per-node warning membership: the union of every warning's nodeIds. Drives the
  // authoring-warning ring on the node card.
  const warningNodeIds = useMemo(() => {
    const set = new Set<string>()
    for (const w of dagWarnings) for (const id of w.nodeIds ?? []) set.add(id)
    return set
  }, [dagWarnings])

  // Nodes handed to ReactFlow, with the derived hasWarning flag merged in. Kept
  // separate from `nodes` state so flagging never triggers a state-update loop.
  const flowNodes = useMemo(
    () => nodes.map((n) => (warningNodeIds.has(n.id) === !!n.data.hasWarning
      ? n
      : { ...n, data: { ...n.data, hasWarning: warningNodeIds.has(n.id) } })),
    [nodes, warningNodeIds],
  )

  // Center + select a node from a warning list item (React Flow flow-coords).
  const focusNode = useCallback(
    (nodeId: string) => {
      const n = nodes.find((x) => x.id === nodeId)
      if (!n) return
      setSelectedId(nodeId)
      setSelectedEdgeId(null)
      setCenter(n.position.x + 100, n.position.y + 40, { zoom: 1.2, duration: 400 })
    },
    [nodes, setCenter],
  )

  return (
    <div className="flex h-[640px] gap-3">
      {/* ─── Palette ─── */}
      <div className="flex h-full w-56 shrink-0 flex-col overflow-hidden rounded-md border bg-card">
        <div className="border-b p-2">
          <Input
            value={paletteFilter}
            onChange={(e) => setPaletteFilter(e.target.value)}
            placeholder="Search nodes…"
            className="h-8 text-xs"
          />
        </div>
        <ScrollArea className="min-h-0 flex-1">
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
          nodes={flowNodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onConnect={readOnly ? undefined : onConnect}
          onInit={(inst) => (rfInstance.current = inst)}
          onDrop={readOnly ? undefined : onDrop}
          onDragOver={readOnly ? undefined : onDragOver}
          onNodeClick={(_, n) => { setSelectedId(n.id); setSelectedEdgeId(null) }}
          onEdgeClick={onEdgeClick}
          onPaneClick={() => { setSelectedId(null); setSelectedEdgeId(null) }}
          nodeTypes={nodeTypes}
          fitView
          proOptions={{ hideAttribution: true }}
          deleteKeyCode={readOnly ? null : ['Backspace', 'Delete']}
          nodesConnectable={!readOnly}
          nodesDraggable={!readOnly}
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
          <DropdownMenu>
            <DropdownMenuTrigger className="inline-flex h-8 items-center rounded-md border bg-background px-3 text-sm font-medium transition-colors hover:bg-accent">
              Presets ▾
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-72">
              <DropdownMenuLabel>Load a starter flow</DropdownMenuLabel>
              <DropdownMenuSeparator />
              {QUEST_PRESETS.map((preset) => {
                const loadable = presetIsLoadable(preset)
                return (
                  <DropdownMenuItem
                    key={preset.id}
                    disabled={!loadable}
                    onClick={() => loadPreset(preset)}
                    className="flex-col items-start gap-0.5"
                  >
                    <span className="flex w-full items-center gap-1.5 text-xs font-medium">
                      {preset.name}
                      {preset.requiresChain && (
                        <Badge variant="outline" className="text-[8px]">on-chain</Badge>
                      )}
                    </span>
                    <span className="text-[10px] text-muted-foreground">{preset.description}</span>
                  </DropdownMenuItem>
                )
              })}
            </DropdownMenuContent>
          </DropdownMenu>
          <Button size="sm" variant="outline" onClick={autoLayout} disabled={nodes.length === 0}>
            Auto-layout
          </Button>
          <Button size="sm" variant="outline" onClick={clearAll} disabled={nodes.length === 0}>
            Clear
          </Button>
        </div>
      </div>

      {/* ─── Inspector ─── */}
      <div className="flex h-full w-72 shrink-0 flex-col overflow-hidden rounded-md border bg-card">
        <div className="border-b p-2">
          <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            {selectedNode ? 'Node Config' : selectedEdge ? 'Edge Config' : 'Inspector'}
          </span>
        </div>
        <ScrollArea className="min-h-0 flex-1">
          <div className="space-y-3 p-3">
            {selectedNode && !readOnly ? (
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
            ) : selectedEdge ? (
              // ─── Edge inspector (G2) ───
              <EdgeInspector
                edge={selectedEdge}
                onPatch={readOnly ? undefined : patchEdge}
                onDelete={readOnly ? undefined : deleteSelectedEdge}
              />
            ) : (
              <p className="text-xs text-muted-foreground">
                {readOnly
                  ? 'This quest is Active — unpublish it before making changes.'
                  : `Select a node to edit its config or click an edge to change its type. Drag from a node's bottom handle to another node's top handle to create an edge.`}
              </p>
            )}
          </div>
        </ScrollArea>

        <div className="space-y-1 border-t p-2 text-[11px] text-muted-foreground">
          <div className="flex justify-between">
            <span>{nodes.length} nodes · {edges.length} edges</span>
          </div>
          {dagWarnings.length > 0 && (
            <ul className="space-y-0.5 rounded border border-amber-500/40 bg-amber-50 p-1.5 text-[10px] dark:bg-amber-900/20">
              {dagWarnings.map((w, i) => {
                const target = w.nodeIds?.find((id) => nodes.some((n) => n.id === id))
                const color = w.error ? 'text-red-700 dark:text-red-400' : 'text-amber-800 dark:text-amber-300'
                return (
                  <li key={i} className={`flex gap-1 ${color}`}>
                    <span aria-hidden>{w.error ? '✕' : '⚠'}</span>
                    {target ? (
                      <button
                        type="button"
                        onClick={() => focusNode(target)}
                        className="text-left underline decoration-dotted underline-offset-2 hover:decoration-solid"
                        title="Jump to the offending node"
                      >
                        {w.text}
                      </button>
                    ) : (
                      <span>{w.text}</span>
                    )}
                  </li>
                )
              })}
            </ul>
          )}
          <Button
            className="w-full"
            size="sm"
            disabled={submitting || nodes.length === 0 || nodesWithInvalidConfig.length > 0 || conditionalEdgeMissingCondition.length > 0}
            onClick={handleSubmit}
            title={
              nodesWithInvalidConfig.length > 0
                ? 'Fix invalid JSON config before submitting'
                : conditionalEdgeMissingCondition.length > 0
                ? 'Add condition text to all Conditional edges before submitting'
                : undefined
            }
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

// ─── Edge inspector sub-component (G2) ───

interface EdgeInspectorProps {
  edge: Edge
  onPatch?: (patch: Partial<EdgeData>) => void
  onDelete?: () => void
}

function EdgeInspector({ edge, onPatch, onDelete }: EdgeInspectorProps) {
  const data = (edge.data as EdgeData) ?? {}
  const edgeType = data.edgeType ?? 'Control'
  const condition = data.condition ?? ''
  const missingCondition = edgeType === 'Conditional' && !condition.trim()

  return (
    <div className="space-y-3">
      <div className="space-y-1.5">
        <Label className="text-xs">Edge Type</Label>
        <div className="flex gap-2">
          {(['Control', 'Conditional', 'OnFailure'] as const).map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => onPatch?.({ edgeType: t })}
              disabled={!onPatch}
              className={`rounded border px-2 py-1 text-xs transition-colors ${
                edgeType === t
                  ? 'border-primary bg-primary text-primary-foreground'
                  : 'border-border bg-background hover:bg-accent'
              } disabled:opacity-50`}
            >
              {t}
            </button>
          ))}
        </div>
      </div>

      {edgeType === 'Conditional' && (
        <div className="space-y-1.5">
          <div className="flex items-center justify-between">
            <Label className="text-xs">Condition</Label>
            {missingCondition && (
              <span className="text-[10px] text-red-600">required</span>
            )}
          </div>
          <Input
            value={condition}
            onChange={(e) => onPatch?.({ condition: e.target.value })}
            disabled={!onPatch}
            placeholder="e.g. true, false, output.ok == true"
            className={`h-8 text-xs ${missingCondition ? 'border-red-500' : ''}`}
          />
          {missingCondition && (
            <p className="text-[10px] text-red-600">
              Conditional edges require a non-empty condition. The server will reject this edge.
            </p>
          )}
        </div>
      )}

      {onDelete && (
        <Button size="sm" variant="destructive" className="w-full" onClick={onDelete}>
          Delete Edge
        </Button>
      )}
    </div>
  )
}
