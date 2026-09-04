'use client'

import { useCallback, useRef } from 'react'
import {
  MarkerType,
  addEdge,
  useEdgesState,
  useNodesState,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
  type ReactFlowInstance,
} from '@xyflow/react'
import type { QuestNodeData } from '../quest-node'
import { layoutGraph } from '../layout'
import { NODE_CATALOG_BY_TYPE } from '../node-catalog'
import { edgeStyle, type EdgeData } from '../edge-model'
import type { PaletteEntry } from '../node-palette'
import type { QuestPreset } from '../presets'
import { useGraphHistory } from './use-graph-history'
import type { SubgraphClipboard } from './use-subgraph-clipboard'

let idCounter = 0
const nextId = () => `n${++idCounter}_${Math.round(performance.now())}`

/** Pixels each successive paste of the same clipboard is nudged down-right. */
const PASTE_OFFSET_STEP = 48

/** Auto-uniquify a label against a live label set: node names are the `$from` binding key, and the server rejects duplicates at publish. Appends " 2"/" 3"/… on collision. */
function uniquifyLabel(existingLabels: Set<string>, baseLabel: string): string {
  let label = baseLabel
  let suffix = 2
  while (existingLabels.has(label)) label = `${baseLabel} ${suffix++}`
  return label
}

interface QuestGraphStateOptions {
  setSelectedNodeId: (nodeId: string | null) => void
  setSelectedEdgeId: (edgeId: string | null) => void
}

/** Canvas nodes/edges plus every graph mutation (add, connect, patch, delete, layout, preset, paste) and its undo/redo history. See `../AGENTS.md` §graph-state. */
export function useQuestGraphState({ setSelectedNodeId, setSelectedEdgeId }: QuestGraphStateOptions) {
  const [nodes, setNodes, applyNodesChangeInternal] = useNodesState<Node<QuestNodeData>>([])
  const [edges, setEdges, applyEdgesChangeInternal] = useEdgesState<Edge>([])
  const reactFlowInstance = useRef<ReactFlowInstance<Node<QuestNodeData>, Edge> | null>(null)

  // How many times the CURRENT clipboard has been pasted. A clipboard is a frozen
  // snapshot that never moves, so offsetting from it by a constant drops every repeat
  // paste on the exact same spot; the run counter steps them apart instead. Keyed on
  // clipboard identity so a fresh copy restarts the run. See `../AGENTS.md` §clipboard.
  const pasteRun = useRef<{ clipboard: SubgraphClipboard | null; count: number }>({
    clipboard: null,
    count: 0,
  })

  // Destructured (not kept as `history.pushHistory`) so `exhaustive-deps` treats
  // `pushHistory` as the plain stable value it is — a member-expression dependency
  // doesn't read as stable to the linter even though the underlying function never
  // changes identity. See `../AGENTS.md` §history.
  const { pushHistory, undo, redo, canUndo, canRedo } = useGraphHistory({
    nodes,
    edges,
    setNodes,
    setEdges,
    setSelectedNodeId,
    setSelectedEdgeId,
  })

  const registerReactFlowInstance = useCallback(
    (instance: ReactFlowInstance<Node<QuestNodeData>, Edge>) => {
      reactFlowInstance.current = instance
    },
    [],
  )

  // Wrap React Flow's own change handlers so the native Backspace/Delete key path
  // (which never calls our `deleteNode`/`deleteEdge`) is undoable too. Only 'remove'
  // changes push history — drag/resize/select changes fire far too often to snapshot.
  const onNodesChange = useCallback(
    (changes: NodeChange<Node<QuestNodeData>>[]) => {
      if (changes.some((c) => c.type === 'remove')) pushHistory()
      applyNodesChangeInternal(changes)
    },
    [pushHistory, applyNodesChangeInternal],
  )

  const onEdgesChange = useCallback(
    (changes: EdgeChange<Edge>[]) => {
      if (changes.some((c) => c.type === 'remove')) pushHistory()
      applyEdgesChangeInternal(changes)
    },
    [pushHistory, applyEdgesChangeInternal],
  )

  const addNode = useCallback(
    (entry: PaletteEntry, position?: { x: number; y: number }) => {
      pushHistory()
      const id = nextId()
      // Functional updater reads the live array from React's queue, not a closed-over
      // `nodes` — see ../AGENTS.md §known-quirks for the stale-closure bug this avoids.
      setNodes((nds) => {
        const pos = position ?? { x: 120 + nds.length * 30, y: 80 + nds.length * 30 }
        const isFirst = nds.length === 0
        const baseLabel = entry.templateName ?? entry.label
        const label = uniquifyLabel(new Set(nds.map((n) => n.data.label)), baseLabel)
        return nds.concat({
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
        })
      })
      setSelectedNodeId(id)
    },
    [pushHistory, setNodes, setSelectedNodeId],
  )

  const connectNodes = useCallback(
    (connection: Connection) => {
      pushHistory()
      setEdges((eds) =>
        addEdge(
          {
            ...connection,
            markerEnd: { type: MarkerType.ArrowClosed },
            ...edgeStyle('Control'),
            data: { edgeType: 'Control' } satisfies EdgeData,
          },
          eds,
        ),
      )
    },
    [pushHistory, setEdges],
  )

  const patchNode = useCallback(
    (nodeId: string, patch: Partial<QuestNodeData>) => {
      pushHistory()
      setNodes((nds) => nds.map((n) => (n.id === nodeId ? { ...n, data: { ...n.data, ...patch } } : n)))
    },
    [pushHistory, setNodes],
  )

  const patchEdge = useCallback(
    (edgeId: string, patch: Partial<EdgeData>) => {
      pushHistory()
      setEdges((eds) =>
        eds.map((e) => {
          if (e.id !== edgeId) return e
          const nextData: EdgeData = { ...(e.data as EdgeData), ...patch }
          // Non-Conditional edges must not carry condition text (backend rejects it).
          if (patch.edgeType !== undefined && patch.edgeType !== 'Conditional') {
            nextData.condition = undefined
          }
          return { ...e, data: nextData, ...edgeStyle(nextData.edgeType ?? 'Control') } as Edge
        }),
      )
    },
    [pushHistory, setEdges],
  )

  const deleteNode = useCallback(
    (nodeId: string) => {
      pushHistory()
      setNodes((nds) => nds.filter((n) => n.id !== nodeId))
      setEdges((eds) => eds.filter((e) => e.source !== nodeId && e.target !== nodeId))
      setSelectedNodeId(null)
    },
    [pushHistory, setNodes, setEdges, setSelectedNodeId],
  )

  const deleteEdge = useCallback(
    (edgeId: string) => {
      pushHistory()
      setEdges((eds) => eds.filter((e) => e.id !== edgeId))
      setSelectedEdgeId(null)
    },
    [pushHistory, setEdges, setSelectedEdgeId],
  )

  const autoLayout = useCallback(() => {
    pushHistory()
    setNodes((nds) => layoutGraph(nds, edges) as Node<QuestNodeData>[])
    window.setTimeout(() => reactFlowInstance.current?.fitView({ padding: 0.2 }), 0)
  }, [pushHistory, edges, setNodes])

  const clearGraph = useCallback(() => {
    pushHistory()
    setNodes([])
    setEdges([])
    setSelectedNodeId(null)
    setSelectedEdgeId(null)
  }, [pushHistory, setNodes, setEdges, setSelectedNodeId, setSelectedEdgeId])

  const pasteSubgraph = useCallback(
    (clipboard: SubgraphClipboard | null) => {
      if (!clipboard || clipboard.nodes.length === 0) return
      pushHistory()

      // Fresh ids up front so the edge remap below never depends on read-after-write
      // ordering inside the functional updater.
      const idByOriginalId = new Map(clipboard.nodes.map((n) => [n.sourceId, nextId()]))

      // Computed in the action body, never inside the `setNodes` updater below —
      // StrictMode double-invokes updaters, which would double-count the run.
      if (pasteRun.current.clipboard !== clipboard) pasteRun.current = { clipboard, count: 0 }
      pasteRun.current.count += 1
      const pasteOffset = PASTE_OFFSET_STEP * pasteRun.current.count

      setNodes((nds) => {
        // Uniqueness holds across the WHOLE resulting graph — existing nodes AND the
        // rest of this same paste batch — since `$from "upstream.<name>"` bindings are
        // name-keyed and the server rejects duplicates at publish.
        const existingLabels = new Set(nds.map((n) => n.data.label))
        const pasted: Node<QuestNodeData>[] = clipboard.nodes.map((n) => {
          const label = uniquifyLabel(existingLabels, n.data.label)
          existingLabels.add(label)
          return {
            id: idByOriginalId.get(n.sourceId)!,
            type: 'quest',
            position: { x: n.position.x + pasteOffset, y: n.position.y + pasteOffset },
            // A pasted copy is a detached subgraph, never wired to the rest of the
            // canvas yet — carrying over Entry/Terminal would mint a second Entry
            // node, which the DAG pre-check doesn't flag but the execution model
            // only ever expects one of. Re-mark deliberately via the inspector.
            data: { ...n.data, label, isEntry: false, isTerminal: false },
            selected: true,
          }
        })
        return nds.map((n) => (n.selected ? { ...n, selected: false } : n)).concat(pasted)
      })

      const pastedEdges: Edge[] = clipboard.edges.flatMap((e) => {
        const source = idByOriginalId.get(e.sourceId)
        const target = idByOriginalId.get(e.targetId)
        if (!source || !target) return []
        return [
          {
            id: `e_${source}_${target}`,
            source,
            target,
            markerEnd: { type: MarkerType.ArrowClosed },
            ...edgeStyle(e.data.edgeType ?? 'Control'),
            data: { ...e.data } satisfies EdgeData,
          } as Edge,
        ]
      })
      setEdges((eds) => eds.concat(pastedEdges))

      setSelectedNodeId(null)
      setSelectedEdgeId(null)
    },
    [pushHistory, setNodes, setEdges, setSelectedNodeId, setSelectedEdgeId],
  )

  const loadPreset = useCallback(
    (preset: QuestPreset) => {
      pushHistory()
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
      setSelectedNodeId(null)
      setSelectedEdgeId(null)
      window.setTimeout(() => reactFlowInstance.current?.fitView({ padding: 0.2 }), 0)
    },
    [pushHistory, setNodes, setEdges, setSelectedNodeId, setSelectedEdgeId],
  )

  return {
    nodes,
    edges,
    onNodesChange,
    onEdgesChange,
    registerReactFlowInstance,
    addNode,
    connectNodes,
    patchNode,
    patchEdge,
    deleteNode,
    deleteEdge,
    autoLayout,
    clearGraph,
    loadPreset,
    pasteSubgraph,
    undo,
    redo,
    canUndo,
    canRedo,
  }
}
