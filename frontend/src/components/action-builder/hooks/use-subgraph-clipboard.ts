'use client'

import { useCallback, useState } from 'react'
import type { Edge, Node } from '@xyflow/react'
import type { ActionNodeData } from '../action-node'
import type { EdgeData } from '../edge-model'

/** One copied node, keyed by its ORIGINAL canvas id so `pasteSubgraph` can remap internal edges. */
export interface ClipboardNode {
  sourceId: string
  position: { x: number; y: number }
  data: ActionNodeData
}

export interface ClipboardEdge {
  sourceId: string
  targetId: string
  data: EdgeData
}

export interface SubgraphClipboard {
  nodes: ClipboardNode[]
  edges: ClipboardEdge[]
}

interface SubgraphClipboardOptions {
  nodes: Node<ActionNodeData>[]
  edges: Edge[]
  selectedNodeIds: Set<string>
}

/**
 * In-memory clipboard for subgraph copy/paste. Copy is a pure read of the current
 * selection; paste (fresh ids, remapped edges, unique labels) lives in
 * `useActionGraphState.pasteSubgraph` since only that hook can see the FULL node
 * set a pasted label must stay unique against. See `../AGENTS.md` §clipboard.
 */
export function useSubgraphClipboard({ nodes, edges, selectedNodeIds }: SubgraphClipboardOptions) {
  const [clipboard, setClipboard] = useState<SubgraphClipboard | null>(null)

  const copySelection = useCallback(() => {
    if (selectedNodeIds.size === 0) return
    const copiedNodes = nodes.filter((n) => selectedNodeIds.has(n.id))
    if (copiedNodes.length === 0) return
    // Only edges with BOTH endpoints in the selection travel with the copy — an
    // edge pointing outside it would dangle in the detached paste.
    const copiedEdges = edges.filter((e) => selectedNodeIds.has(e.source) && selectedNodeIds.has(e.target))
    setClipboard({
      nodes: copiedNodes.map((n) => ({ sourceId: n.id, position: { ...n.position }, data: { ...n.data } })),
      edges: copiedEdges.map((e) => ({ sourceId: e.source, targetId: e.target, data: { ...(e.data as EdgeData) } })),
    })
  }, [nodes, edges, selectedNodeIds])

  return { clipboard, copySelection, hasClipboard: !!clipboard && clipboard.nodes.length > 0 }
}
