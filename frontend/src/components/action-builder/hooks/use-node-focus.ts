'use client'

import { useCallback } from 'react'
import { useReactFlow, type Node } from '@xyflow/react'
import type { ActionNodeData } from '../action-node'

interface NodeFocusOptions {
  nodes: Node<ActionNodeData>[]
  selectNode: (nodeId: string) => void
}

/** Center + select a node from a warning list item (React Flow flow-coords). */
export function useNodeFocus({ nodes, selectNode }: NodeFocusOptions) {
  const { setCenter } = useReactFlow()

  return useCallback(
    (nodeId: string) => {
      const node = nodes.find((n) => n.id === nodeId)
      if (!node) return
      selectNode(nodeId)
      setCenter(node.position.x + 100, node.position.y + 40, { zoom: 1.2, duration: 400 })
    },
    [nodes, selectNode, setCenter],
  )
}
