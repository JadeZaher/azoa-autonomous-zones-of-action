'use client'

import { useCallback, useEffect, useRef, useState } from 'react'
import type { Edge, Node } from '@xyflow/react'
import type { ActionNodeData } from '../action-node'

const HISTORY_LIMIT = 50

interface GraphSnapshot {
  nodes: Node<ActionNodeData>[]
  edges: Edge[]
}

interface GraphHistoryOptions {
  nodes: Node<ActionNodeData>[]
  edges: Edge[]
  setNodes: (nodes: Node<ActionNodeData>[]) => void
  setEdges: (edges: Edge[]) => void
  setSelectedNodeId: (nodeId: string | null) => void
  setSelectedEdgeId: (edgeId: string | null) => void
}

/**
 * Undo/redo over the canvas's node/edge arrays. See `../AGENTS.md` §history for
 * why `pushHistory` must be called from the action body, never from inside a
 * `setNodes`/`setEdges` functional updater.
 */
export function useGraphHistory({ nodes, edges, setNodes, setEdges, setSelectedNodeId, setSelectedEdgeId }: GraphHistoryOptions) {
  const [past, setPast] = useState<GraphSnapshot[]>([])
  const [future, setFuture] = useState<GraphSnapshot[]>([])

  // "Latest value" refs, synced post-commit (never during render — the
  // react-hooks/refs rule disallows writing `.current` in the render body).
  // Effects run after every commit and strictly before the next real user
  // event, so by the time a click calls pushHistory these are never stale;
  // no dependency array keeps pushHistory's own identity permanently stable,
  // which is what lets every mutator list it as a dep without churn.
  const nodesRef = useRef(nodes)
  const edgesRef = useRef(edges)
  useEffect(() => {
    nodesRef.current = nodes
    edgesRef.current = edges
  })

  const pushHistory = useCallback(() => {
    setPast((prev) => {
      // Coalesce a snapshot that is reference-identical to the newest one. React
      // Flow's `deleteElements` calls `triggerEdgeChanges` and `triggerNodeChanges`
      // as back-to-back SYNCHRONOUS statements, so deleting one connected node runs
      // both of our change wrappers — and therefore pushHistory twice — inside a
      // single tick. No commit can happen between two synchronous statements, so the
      // effect-synced refs still hold the very same arrays both times.
      //
      // Reference identity is the precise test, not a heuristic: every mutator
      // replaces the arrays it touches, so identical refs mean the graph has not
      // changed since the newest snapshot and there is nothing extra to undo TO.
      // Deleting an edge on its own still records normally — only `onEdgesChange`
      // fires there, so there is no earlier snapshot to be identical to.
      // See `../AGENTS.md` §history.
      const newest = prev[prev.length - 1]
      if (newest && newest.nodes === nodesRef.current && newest.edges === edgesRef.current) return prev

      const next = prev.concat({ nodes: nodesRef.current, edges: edgesRef.current })
      return next.length > HISTORY_LIMIT ? next.slice(next.length - HISTORY_LIMIT) : next
    })
    setFuture([])
  }, [])

  const undo = useCallback(() => {
    if (past.length === 0) return
    const previous = past[past.length - 1]
    setFuture((prev) => prev.concat({ nodes: nodesRef.current, edges: edgesRef.current }))
    setPast((prev) => prev.slice(0, -1))
    setNodes(previous.nodes)
    setEdges(previous.edges)
    setSelectedNodeId(null)
    setSelectedEdgeId(null)
  }, [past, setNodes, setEdges, setSelectedNodeId, setSelectedEdgeId])

  const redo = useCallback(() => {
    if (future.length === 0) return
    const next = future[future.length - 1]
    setPast((prev) => prev.concat({ nodes: nodesRef.current, edges: edgesRef.current }))
    setFuture((prev) => prev.slice(0, -1))
    setNodes(next.nodes)
    setEdges(next.edges)
    setSelectedNodeId(null)
    setSelectedEdgeId(null)
  }, [future, setNodes, setEdges, setSelectedNodeId, setSelectedEdgeId])

  return { pushHistory, undo, redo, canUndo: past.length > 0, canRedo: future.length > 0 }
}
