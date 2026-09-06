'use client'

import { useCallback } from 'react'
import type { Edge, Node } from '@xyflow/react'
import type { ActionNodeData } from '../action-node'
import { serializeGraph, type BuiltGraph } from '../graph-serialization'

interface ActionSubmissionOptions {
  nodes: Node<ActionNodeData>[]
  edges: Edge[]
  onSubmit: (graph: BuiltGraph) => void
  nodesWithInvalidConfig: Node<ActionNodeData>[]
  conditionalEdgesMissingCondition: Edge[]
}

/** Submit lifecycle: serialize the graph, plus the blocking reasons the button surfaces. */
export function useActionSubmission({
  nodes,
  edges,
  onSubmit,
  nodesWithInvalidConfig,
  conditionalEdgesMissingCondition,
}: ActionSubmissionOptions) {
  const handleSubmit = useCallback(() => {
    // Hard blocks: invalid config JSON or Conditional edges missing condition
    // will be rejected by the server anyway — surface them here first.
    if (nodesWithInvalidConfig.length > 0) return
    if (conditionalEdgesMissingCondition.length > 0) return
    onSubmit(serializeGraph(nodes, edges))
  }, [nodes, edges, onSubmit, nodesWithInvalidConfig, conditionalEdgesMissingCondition])

  const submitBlockedReason =
    nodesWithInvalidConfig.length > 0
      ? 'Fix invalid JSON config before submitting'
      : conditionalEdgesMissingCondition.length > 0
      ? 'Add condition text to all Conditional edges before submitting'
      : undefined

  return {
    handleSubmit,
    hasBlockingErrors: nodesWithInvalidConfig.length > 0 || conditionalEdgesMissingCondition.length > 0,
    submitBlockedReason,
  }
}
