'use client'

import type { Edge, Node } from '@xyflow/react'
import { ScrollArea } from '@/components/ui/scroll-area'
import type { EdgeData } from '../edge-model'
import type { ActionNodeData } from '../action-node'
import { NodeInspector } from './node-inspector'
import { EdgeInspector } from './edge-inspector'

interface InspectorPanelProps {
  selectedNode: Node<ActionNodeData> | null
  selectedEdge: Edge | null
  onPatchNode: (nodeId: string, patch: Partial<ActionNodeData>) => void
  onDeleteNode: (nodeId: string) => void
  onPatchEdge: (edgeId: string, patch: Partial<EdgeData>) => void
  onDeleteEdge: (edgeId: string) => void
  /** Footer content pinned below the scrolling inspector body. */
  children?: React.ReactNode
}

/** Right column: edits whichever node or edge is selected. See `../AGENTS.md` §selection. */
export function InspectorPanel({
  selectedNode,
  selectedEdge,
  onPatchNode,
  onDeleteNode,
  onPatchEdge,
  onDeleteEdge,
  children,
}: InspectorPanelProps) {
  return (
    <div className="flex h-full w-full flex-col overflow-hidden rounded-md border bg-card">
      <div className="border-b p-2">
        <span className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">
          {selectedNode ? 'Node Config' : selectedEdge ? 'Edge Config' : 'Inspector'}
        </span>
      </div>
      <ScrollArea className="min-h-0 flex-1">
        <div className="space-y-3 p-3">
          {selectedNode ? (
            <NodeInspector
              node={selectedNode}
              onPatch={(patch) => onPatchNode(selectedNode.id, patch)}
              onDelete={() => onDeleteNode(selectedNode.id)}
            />
          ) : selectedEdge ? (
            <EdgeInspector
              edge={selectedEdge}
              onPatch={(patch) => onPatchEdge(selectedEdge.id, patch)}
              onDelete={() => onDeleteEdge(selectedEdge.id)}
            />
          ) : (
            <p className="text-xs text-muted-foreground">
              {`Select a node to edit its config or click an edge to change its type. Drag from a node's bottom handle to another node's top handle to create an edge.`}
            </p>
          )}
        </div>
      </ScrollArea>

      {children}
    </div>
  )
}
