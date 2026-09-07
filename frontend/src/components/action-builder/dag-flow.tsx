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
import { ActionNode, type ActionNodeData } from './action-node'
import { layoutGraph } from './layout'
import { categoryFor, type NodeCategory } from './node-catalog'
import { edgeStyle } from './edge-model'

const nodeTypes = { action: ActionNode }

/** Hex equivalents of the category accent colors, for the MiniMap. */
const CATEGORY_HEX: Record<NodeCategory, string> = {
  Holon: '#3b82f6',
  NFT: '#a855f7',
  Wallet: '#10b981',
  STAR: '#f59e0b',
  Search: '#06b6d4',
  User: '#ec4899',
  Blockchain: '#f97316',
  Control: '#64748b',
  Economic: '#f43f5e',
  // Phase 5 D20/D21 rails. Record<NodeCategory, string> is exhaustive by design:
  // adding a category without a colour here is a compile error, which is how this
  // miss surfaced.
  Governance: '#8b5cf6',
  Lending: '#14b8a6',
}

/** Shape of a node as returned by the backend on a fetched action. */
interface RunNode {
  id: string
  name: string
  nodeType: string
  state?: string
  isEntry: boolean
  isTerminal: boolean
  output?: string
  error?: string
}

export interface RunEdge {
  id: string
  sourceNodeId: string
  targetNodeId: string
  edgeType: string
  condition?: string
}

/**
 * Read-only React Flow render of a fetched action's DAG, used in the
 * "My Actions" detail panel. Auto-lays-out by execution layering.
 */
export function DagFlow({ nodes, edges }: { nodes: RunNode[]; edges: RunEdge[] }) {
  const flowNodes = useMemo<Node<ActionNodeData>[]>(() => {
    const ns: Node<ActionNodeData>[] = nodes.map((n) => ({
      id: n.id,
      type: 'action',
      position: { x: 0, y: 0 },
      data: {
        label: n.name,
        nodeType: n.nodeType,
        config: '',
        isEntry: n.isEntry,
        isTerminal: n.isTerminal,
        state: n.state,
        output: n.output,
        error: n.error,
      },
    }))
    const es: Edge[] = edges.map(toFlowEdge)
    return layoutGraph(ns, es) as Node<ActionNodeData>[]
  }, [nodes, edges])

  const flowEdges = useMemo<Edge[]>(() => edges.map(toFlowEdge), [edges])

  if (nodes.length === 0) {
    return <p className="py-8 text-center text-sm text-muted-foreground">This action has no nodes.</p>
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
          nodeColor={(n) => CATEGORY_HEX[categoryFor((n.data as ActionNodeData)?.nodeType ?? '')]}
          nodeStrokeWidth={2}
          className="!bg-muted"
        />
      </ReactFlow>
    </div>
  )
}

/**
 * One fetched action edge → a React Flow edge. Exported for unit test: the spread
 * ordering below is the whole point and is invisible from the rendered output.
 */
export function toFlowEdge(e: RunEdge): Edge {
  const conditional = e.edgeType === 'Conditional'
  const onFailure = e.edgeType === 'OnFailure'
  return {
    id: e.id,
    source: e.sourceNodeId,
    target: e.targetNodeId,
    markerEnd: { type: MarkerType.ArrowClosed },
    // Stroke/dash/animation come from the shared edge-model table (edge-model.ts).
    // This spread MUST stay above `label`: `edgeStyle` is typed to return a `label`
    // too, so the day the style table starts setting one it would silently blank
    // every label this read-only render computes.
    ...edgeStyle(e.edgeType),
    label: conditional ? (e.condition ?? 'if') : onFailure ? 'on failure' : undefined,
  }
}
