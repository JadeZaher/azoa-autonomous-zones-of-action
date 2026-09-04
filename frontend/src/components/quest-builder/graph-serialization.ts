/** Canvas graph → backend create payload. See `AGENTS.md` §serialization. */

import type { Edge, Node } from '@xyflow/react'
import type { QuestNodeData } from './quest-node'
import type { EdgeData } from './edge-model'

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

/** Serialize canvas nodes/edges into the backend create payload. */
export function serializeGraph(nodes: Node<QuestNodeData>[], edges: Edge[]): BuiltGraph {
  const indexOf = new Map(nodes.map((n, i) => [n.id, i]))
  return {
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
}
