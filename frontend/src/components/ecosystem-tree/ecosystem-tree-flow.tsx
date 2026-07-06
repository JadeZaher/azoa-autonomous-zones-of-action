'use client'

import { useMemo } from 'react'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  MarkerType,
  type Edge,
  type Node,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { EcosystemNode, type EcosystemNodeData } from './ecosystem-node'
// Reuse the quest-builder longest-path layout (pure helper, read-only import).
import { layoutGraph } from '../quest-builder/layout'

const nodeTypes = { ecosystem: EcosystemNode }

/** Recursive tree node shape as returned by the backend GetEcosystem endpoint. */
export interface EcosystemTreeNode {
  node: {
    id: string
    ecosystemId: string
    parentNodeId?: string | null
    refKind: string
    refId: string
    label?: string | null
  }
  children: EcosystemTreeNode[]
}

export interface EcosystemTree {
  ecosystem: {
    id: string
    name: string
    targetChain?: string | null
  }
  roots: EcosystemTreeNode[]
}

const KIND_HEX: Record<string, string> = {
  DappSeries: '#f59e0b',
  StarOdk: '#6366f1',
}

/** Flattens the recursive tree into React Flow nodes + parent→child edges. */
function flatten(roots: EcosystemTreeNode[]): {
  nodes: Node<EcosystemNodeData>[]
  edges: Edge[]
} {
  const nodes: Node<EcosystemNodeData>[] = []
  const edges: Edge[] = []
  const seen = new Set<string>()

  const walk = (tn: EcosystemTreeNode, isRoot: boolean) => {
    if (seen.has(tn.node.id)) return // defensive: backend guards cycles, but never loop the UI
    seen.add(tn.node.id)

    nodes.push({
      id: tn.node.id,
      type: 'ecosystem',
      position: { x: 0, y: 0 },
      data: {
        label: tn.node.label || `${tn.node.refKind} ${tn.node.refId.slice(0, 8)}`,
        refKind: tn.node.refKind,
        refId: tn.node.refId,
        isRoot,
      },
    })

    for (const child of tn.children) {
      edges.push({
        id: `${tn.node.id}->${child.node.id}`,
        source: tn.node.id,
        target: child.node.id,
        markerEnd: { type: MarkerType.ArrowClosed },
        style: { stroke: '#94a3b8' },
      })
      walk(child, false)
    }
  }

  for (const r of roots) walk(r, true)
  return { nodes, edges }
}

/**
 * Read-only React Flow render of a STARODK ecosystem tree. Auto-lays-out with
 * the quest-builder's longest-path layering, so nesting depth reads top→bottom.
 */
export function EcosystemTreeFlow({ tree }: { tree: EcosystemTree }) {
  const { flowNodes, flowEdges } = useMemo(() => {
    const { nodes, edges } = flatten(tree.roots)
    return {
      flowNodes: layoutGraph(nodes, edges) as Node<EcosystemNodeData>[],
      flowEdges: edges,
    }
  }, [tree])

  if (tree.roots.length === 0) {
    return (
      <p className="py-8 text-center text-sm text-muted-foreground">
        This ecosystem has no dApps yet. Attach a DappSeries to begin.
      </p>
    )
  }

  return (
    <div className="h-[460px] w-full overflow-hidden rounded-md border">
      <ReactFlow
        nodes={flowNodes}
        edges={flowEdges}
        nodeTypes={nodeTypes}
        fitView
        fitViewOptions={{ padding: 0.2 }}
        proOptions={{ hideAttribution: true }}
        nodesDraggable
        nodesConnectable={false}
        elementsSelectable={false}
      >
        <Background gap={16} />
        <Controls showInteractive={false} />
        <MiniMap
          pannable
          zoomable
          nodeColor={(n) => KIND_HEX[(n.data as EcosystemNodeData)?.refKind] ?? '#94a3b8'}
          nodeStrokeWidth={2}
          className="!bg-muted"
        />
      </ReactFlow>
    </div>
  )
}
