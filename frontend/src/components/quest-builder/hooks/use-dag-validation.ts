'use client'

import { useMemo } from 'react'
import type { Edge, Node } from '@xyflow/react'
import type { QuestNodeData } from '../quest-node'
import type { EdgeData } from '../edge-model'
import { isValidJsonConfig } from '../dag-analysis'
import { computeDagWarnings, type DagWarning } from '../dag-warnings'

/** Stable "no warnings" reference so `useWarningFlaggedNodes` can no-op nodes that stay clean between renders. */
const EMPTY_NODE_WARNINGS: DagWarning[] = []

interface DagValidationOptions {
  nodes: Node<QuestNodeData>[]
  edges: Edge[]
}

/** Client-side DAG pre-check: blocking conditions plus the full warning list. See `../AGENTS.md` §dag-precheck. */
export function useDagValidation({ nodes, edges }: DagValidationOptions) {
  // Conditional edge with empty condition is a server hard-reject (FR-1c / FR-8b).
  const conditionalEdgesMissingCondition = useMemo(() =>
    edges.filter((e) => (e.data as EdgeData)?.edgeType === 'Conditional' && !((e.data as EdgeData)?.condition ?? '').trim()),
  [edges])

  // Any node with invalid JSON config blocks submit (G3 — config validity pre-submit).
  const nodesWithInvalidConfig = useMemo(() =>
    nodes.filter((n) => !isValidJsonConfig(n.data.config)),
  [nodes])

  const warnings = useMemo(
    () => computeDagWarnings({ nodes, edges, conditionalEdgesMissingCondition, nodesWithInvalidConfig }),
    [nodes, edges, conditionalEdgesMissingCondition, nodesWithInvalidConfig],
  )

  // Per-node warning index: every warning that names a given node, keyed by that
  // node's id. Drives both the authoring-warning ring/badge on the node card and
  // (via the footer) the reverse click-to-highlight path.
  const nodeWarningsById = useMemo(() => {
    const map = new Map<string, DagWarning[]>()
    for (const w of warnings) {
      for (const id of w.nodeIds ?? []) {
        const list = map.get(id)
        if (list) list.push(w)
        else map.set(id, [w])
      }
    }
    return map
  }, [warnings])

  return { conditionalEdgesMissingCondition, nodesWithInvalidConfig, warnings, nodeWarningsById }
}

/**
 * Nodes handed to ReactFlow, with the per-node warning list merged into `data.warnings`.
 * Kept separate from graph state so flagging never triggers a state-update loop.
 */
export function useWarningFlaggedNodes(nodes: Node<QuestNodeData>[], nodeWarningsById: Map<string, DagWarning[]>) {
  return useMemo(
    () =>
      nodes.map((n) => {
        const nodeWarnings = nodeWarningsById.get(n.id) ?? EMPTY_NODE_WARNINGS
        return n.data.warnings === nodeWarnings ? n : { ...n, data: { ...n.data, warnings: nodeWarnings } }
      }),
    [nodes, nodeWarningsById],
  )
}
