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
import { QuestNode, type QuestNodeData } from './quest-node'
import { layoutGraph } from './layout'
import { categoryFor, type NodeCategory } from './node-catalog'

const nodeTypes = { quest: QuestNode }

/** Hex equivalents of the category accent colors, for the MiniMap. */
const CATEGORY_HEX: Record<NodeCategory, string> = {
  Holon: '#3b82f6',
  NFT: '#a855f7',
  Wallet: '#10b981',
  STAR: '#f59e0b',
  Search: '#06b6d4',
  Avatar: '#ec4899',
  Blockchain: '#f97316',
  Control: '#64748b',
  Economic: '#f43f5e',
}

/** Shape of a node as returned by the backend on a fetched quest. */
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

interface RunEdge {
  id: string
  sourceNodeId: string
  targetNodeId: string
  edgeType: string
  condition?: string
}

/**
 * Read-only React Flow render of a fetched quest's DAG, used in the
 * "My Quests" detail panel. Auto-lays-out by execution layering.
 */
export function DagFlow({ nodes, edges }: { nodes: RunNode[]; edges: RunEdge[] }) {
  const flowNodes = useMemo<Node<QuestNodeData>[]>(() => {
    const ns: Node<QuestNodeData>[] = nodes.map((n) => ({
      id: n.id,
      type: 'quest',
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
    return layoutGraph(ns, es) as Node<QuestNodeData>[]
  }, [nodes, edges])

  const flowEdges = useMemo<Edge[]>(() => edges.map(toFlowEdge), [edges])

  if (nodes.length === 0) {
    return <p className="py-8 text-center text-sm text-muted-foreground">This quest has no nodes.</p>
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
          nodeColor={(n) => CATEGORY_HEX[categoryFor((n.data as QuestNodeData)?.nodeType ?? '')]}
          nodeStrokeWidth={2}
          className="!bg-muted"
        />
      </ReactFlow>
    </div>
  )
}

function toFlowEdge(e: RunEdge): Edge {
  const conditional = e.edgeType === 'Conditional'
  const onFailure = e.edgeType === 'OnFailure'
  // OnFailure = red dashed error-routing edge (mirrors quest-canvas edgeStyle).
  const style = conditional
    ? { stroke: '#f59e0b', strokeDasharray: '5 5' }
    : onFailure
    ? { stroke: '#ef4444', strokeDasharray: '4 4' }
    : { stroke: '#94a3b8' }
  return {
    id: e.id,
    source: e.sourceNodeId,
    target: e.targetNodeId,
    label: conditional ? (e.condition ?? 'if') : onFailure ? 'on failure' : undefined,
    animated: conditional,
    style,
    markerEnd: { type: MarkerType.ArrowClosed },
  }
}
