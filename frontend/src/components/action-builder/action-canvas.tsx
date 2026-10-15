'use client'

import { ReactFlowProvider } from '@xyflow/react'
import { useActionSelection } from './hooks/use-action-selection'
import { useActionGraphState } from './hooks/use-action-graph-state'
import { useNodePalette } from './hooks/use-node-palette'
import { usePaletteDragAndDrop } from './hooks/use-palette-drag-and-drop'
import { useDagValidation, useWarningFlaggedNodes } from './hooks/use-dag-validation'
import { useActionSubmission } from './hooks/use-action-submission'
import { useNodeFocus } from './hooks/use-node-focus'
import { useSubgraphClipboard } from './hooks/use-subgraph-clipboard'
import { useCanvasKeyboardShortcuts } from './hooks/use-canvas-keyboard-shortcuts'
import { NodePalettePanel } from './panels/node-palette-panel'
import { ActionFlowCanvas } from './panels/action-flow-canvas'
import { CanvasToolbar } from './panels/canvas-toolbar'
import { InspectorPanel } from './panels/inspector-panel'
import { ActionStatusFooter } from './panels/action-status-footer'
import type { NodeTemplateMeta } from './node-palette'
import type { BuiltGraph } from './graph-serialization'

export type { NodeTemplateMeta } from './node-palette'
export type { BuiltGraph } from './graph-serialization'

interface ActionCanvasProps {
  nodeTemplates: NodeTemplateMeta[]
  /** Called with serialized graph when the user clicks the submit button. */
  onSubmit: (graph: BuiltGraph) => void
  submitting?: boolean
  submitLabel?: string
}

function ActionCanvasInner({ nodeTemplates, onSubmit, submitting, submitLabel = 'Create Action' }: ActionCanvasProps) {
  const selection = useActionSelection()
  const graph = useActionGraphState({
    setSelectedNodeId: selection.setSelectedNodeId,
    setSelectedEdgeId: selection.setSelectedEdgeId,
  })
  const palette = useNodePalette(nodeTemplates)
  const dragAndDrop = usePaletteDragAndDrop({ palette: palette.entries, addNode: graph.addNode })
  const validation = useDagValidation({ nodes: graph.nodes, edges: graph.edges })
  // NOTE: `.selected` is deliberately NOT rewritten on the way into <ReactFlow>.
  // React Flow owns node selection; `graph.onNodesChange` already applies its
  // `select` changes to this array. See `AGENTS.md` §selection.
  const flowNodes = useWarningFlaggedNodes(graph.nodes, validation.nodeWarningsById)
  const focusNode = useNodeFocus({ nodes: graph.nodes, selectNode: selection.selectNode })
  const clipboard = useSubgraphClipboard({
    nodes: graph.nodes,
    edges: graph.edges,
    selectedNodeIds: selection.selectedNodeIds,
  })
  const submission = useActionSubmission({
    nodes: graph.nodes,
    edges: graph.edges,
    onSubmit,
    nodesWithInvalidConfig: validation.nodesWithInvalidConfig,
    conditionalEdgesMissingCondition: validation.conditionalEdgesMissingCondition,
  })

  useCanvasKeyboardShortcuts({
    onUndo: graph.undo,
    onRedo: graph.redo,
    onCopy: clipboard.copySelection,
    onPaste: () => graph.pasteSubgraph(clipboard.clipboard),
  })

  const selectedNode = graph.nodes.find((n) => n.id === selection.selectedNodeId) ?? null
  const selectedEdge = graph.edges.find((e) => e.id === selection.selectedEdgeId) ?? null

  return (
    <div className="flex h-[640px] gap-3">
      <NodePalettePanel
        groupedByCategory={palette.groupedByCategory}
        filter={palette.filter}
        onFilterChange={palette.setFilter}
        onAddNode={graph.addNode}
        onDragStart={dragAndDrop.handleDragStart}
        activeEntry={palette.activeEntry}
        onMoveActive={palette.moveActive}
        onSetActiveEntry={palette.setActiveEntry}
      />

      <ActionFlowCanvas
        nodes={flowNodes}
        edges={graph.edges}
        onNodesChange={graph.onNodesChange}
        onEdgesChange={graph.onEdgesChange}
        onConnect={graph.connectNodes}
        onInit={graph.registerReactFlowInstance}
        onDrop={dragAndDrop.handleDrop}
        onDragOver={dragAndDrop.handleDragOver}
        onSelectNode={selection.selectNode}
        onSelectEdge={selection.selectEdge}
        onClearSelection={selection.clearSelection}
        onSelectionChange={selection.handleSelectionChange}
      >
        <CanvasToolbar
          nodeCount={graph.nodes.length}
          onLoadPreset={graph.loadPreset}
          onAutoLayout={graph.autoLayout}
          onClear={graph.clearGraph}
          onUndo={graph.undo}
          onRedo={graph.redo}
          canUndo={graph.canUndo}
          canRedo={graph.canRedo}
          onCopy={clipboard.copySelection}
          onPaste={() => graph.pasteSubgraph(clipboard.clipboard)}
          canCopy={selection.selectedNodeIds.size > 0}
          canPaste={clipboard.hasClipboard}
        />
      </ActionFlowCanvas>

      <InspectorPanel
        selectedNode={selectedNode}
        selectedEdge={selectedEdge}
        onPatchNode={graph.patchNode}
        onDeleteNode={graph.deleteNode}
        onPatchEdge={graph.patchEdge}
        onDeleteEdge={graph.deleteEdge}
      >
        <ActionStatusFooter
          nodes={graph.nodes}
          edges={graph.edges}
          warnings={validation.warnings}
          onFocusNode={focusNode}
          highlightedNodeId={selection.selectedNodeId}
          submitting={submitting}
          submitLabel={submitLabel}
          hasBlockingErrors={submission.hasBlockingErrors}
          submitBlockedReason={submission.submitBlockedReason}
          onSubmit={submission.handleSubmit}
        />
      </InspectorPanel>
    </div>
  )
}

/** Interactive action DAG builder: palette, React Flow canvas, inspector. See `AGENTS.md`. */
export function ActionCanvas(props: ActionCanvasProps) {
  return (
    <ReactFlowProvider>
      <ActionCanvasInner {...props} />
    </ReactFlowProvider>
  )
}
