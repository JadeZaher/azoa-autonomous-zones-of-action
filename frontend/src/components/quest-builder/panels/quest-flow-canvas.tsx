'use client'

import { useRef } from 'react'
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Connection,
  type Edge,
  type Node,
  type OnEdgesChange,
  type OnNodesChange,
  type OnSelectionChangeFunc,
  type ReactFlowInstance,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { QuestNode, type QuestNodeData } from '../quest-node'

const nodeTypes = { quest: QuestNode }

interface QuestFlowCanvasProps {
  nodes: Node<QuestNodeData>[]
  edges: Edge[]
  onNodesChange: OnNodesChange<Node<QuestNodeData>>
  onEdgesChange: OnEdgesChange<Edge>
  onConnect: (connection: Connection) => void
  onInit: (instance: ReactFlowInstance<Node<QuestNodeData>, Edge>) => void
  onDrop: (event: React.DragEvent) => void
  onDragOver: (event: React.DragEvent) => void
  onSelectNode: (nodeId: string) => void
  onSelectEdge: (edgeId: string) => void
  onClearSelection: () => void
  /** React Flow's native multi-select (click = single, Ctrl/Cmd+click = additive). */
  onSelectionChange: OnSelectionChangeFunc<Node<QuestNodeData>, Edge>
  /** Overlay content rendered above the flow (the toolbar). */
  children?: React.ReactNode
}

/** Center column: the React Flow surface, its empty state, and any overlay children. */
export function QuestFlowCanvas({
  nodes,
  edges,
  onNodesChange,
  onEdgesChange,
  onConnect,
  onInit,
  onDrop,
  onDragOver,
  onSelectNode,
  onSelectEdge,
  onClearSelection,
  onSelectionChange,
  children,
}: QuestFlowCanvasProps) {
  const wrapperRef = useRef<HTMLDivElement>(null)

  return (
    <div ref={wrapperRef} className="relative flex-1 overflow-hidden rounded-md border">
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={onConnect}
        onInit={onInit}
        onDrop={onDrop}
        onDragOver={onDragOver}
        onNodeClick={(_, node) => onSelectNode(node.id)}
        onEdgeClick={(_, edge) => onSelectEdge(edge.id)}
        onPaneClick={onClearSelection}
        onSelectionChange={onSelectionChange}
        nodeTypes={nodeTypes}
        fitView
        proOptions={{ hideAttribution: true }}
        deleteKeyCode={['Backspace', 'Delete']}
      >
        <Background gap={16} />
        <Controls showInteractive={false} />
        <MiniMap pannable zoomable className="!bg-muted" nodeStrokeWidth={2} />
      </ReactFlow>

      {nodes.length === 0 && (
        <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
          <p className="text-sm text-muted-foreground">
            Click or drag a node from the palette to begin.
          </p>
        </div>
      )}

      {children}
    </div>
  )
}
