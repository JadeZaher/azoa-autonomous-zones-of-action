'use client'

import { useCallback, useMemo, useState } from 'react'
import type { Node } from '@xyflow/react'

/** The subset of React Flow's `OnSelectionChangeParams` this hook actually reads. */
interface SelectionChangeParams {
  nodes: Pick<Node, 'id'>[]
}

/**
 * Which node / edge the inspector is editing, plus the multi-node selection used
 * for subgraph copy. See `../AGENTS.md` §selection.
 *
 * `selectedNodeIds` is NOT hand-rolled and NOT authoritative: React Flow owns node
 * selection (it toggles each node's own `.selected` field — plain click =
 * single-select, Ctrl/Cmd+click = additive — and applies those `select` changes to
 * our nodes array through `onNodesChange`). This hook is a read-only MIRROR of that
 * store, observed via the official `onSelectionChange` event, kept only so
 * non-canvas consumers (the copy/paste toolbar) can ask "what is selected?" without
 * reaching into React Flow's internals.
 *
 * The mirror must never be written back onto the nodes array on the way into
 * `<ReactFlow>`. Doing so is a closed loop: the value fed in is the value read back,
 * so it can never leave its seed of `[]` and multi-select silently dies.
 * See `../AGENTS.md` §selection.
 */
export function useQuestSelection() {
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null)
  const [selectedEdgeId, setSelectedEdgeId] = useState<string | null>(null)
  const [selectedNodeIdList, setSelectedNodeIdList] = useState<string[]>([])
  const selectedNodeIds = useMemo(() => new Set(selectedNodeIdList), [selectedNodeIdList])

  const selectNode = useCallback((nodeId: string) => {
    setSelectedNodeId(nodeId)
    setSelectedEdgeId(null)
  }, [])

  const selectEdge = useCallback((edgeId: string) => {
    setSelectedEdgeId(edgeId)
    setSelectedNodeId(null)
    setSelectedNodeIdList([])
  }, [])

  const clearSelection = useCallback(() => {
    setSelectedNodeId(null)
    setSelectedEdgeId(null)
    setSelectedNodeIdList([])
  }, [])

  /**
   * Wired straight to React Flow's `onSelectionChange` — see the doc comment above.
   *
   * Takes React Flow's own `{ nodes }` params object rather than a bare array so the
   * call site can pass this function BY REFERENCE instead of wrapping it in an inline
   * arrow. That matters: `SelectionListenerInner` lists `onSelectionChange` in its
   * effect deps and `<ReactFlow>` is not memoized, so a fresh arrow per render re-runs
   * the effect on every render. See `../AGENTS.md` §selection-render-loop.
   */
  const handleSelectionChange = useCallback(({ nodes }: SelectionChangeParams) => {
    // Bail on an unchanged id list. `nodes.map(...)` is always a fresh array and would
    // never be `Object.is`-equal to the previous state, so without this guard every
    // selection event costs a render even when nothing about the selection changed.
    setSelectedNodeIdList((prev) =>
      prev.length === nodes.length && prev.every((id, i) => id === nodes[i].id)
        ? prev
        : nodes.map((n) => n.id),
    )
  }, [])

  return {
    selectedNodeId,
    selectedEdgeId,
    selectedNodeIds,
    selectNode,
    selectEdge,
    clearSelection,
    handleSelectionChange,
    setSelectedNodeId,
    setSelectedEdgeId,
    setSelectedNodeIdList,
  }
}
