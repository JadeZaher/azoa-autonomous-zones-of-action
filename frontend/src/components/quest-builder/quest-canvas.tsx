'use client'

import { ReactFlowProvider } from '@xyflow/react'
import { useQuestSelection } from './hooks/use-quest-selection'
import { useQuestGraphState } from './hooks/use-quest-graph-state'
import { useNodePalette } from './hooks/use-node-palette'
import { usePaletteDragAndDrop } from './hooks/use-palette-drag-and-drop'
import { useDagValidation, useWarningFlaggedNodes } from './hooks/use-dag-validation'
import { useQuestSubmission } from './hooks/use-quest-submission'
import { useNodeFocus } from './hooks/use-node-focus'
import { useSubgraphClipboard } from './hooks/use-subgraph-clipboard'
import { useCanvasKeyboardShortcuts } from './hooks/use-canvas-keyboard-shortcuts'
import { NodePalettePanel } from './panels/node-palette-panel'
import { QuestFlowCanvas } from './panels/quest-flow-canvas'
import { CanvasToolbar } from './panels/canvas-toolbar'
import { InspectorPanel } from './panels/inspector-panel'
import { QuestStatusFooter } from './panels/quest-status-footer'
import type { NodeTemplateMeta } from './node-palette'
import type { BuiltGraph } from './graph-serialization'

export type { NodeTemplateMeta } from './node-palette'
export type { BuiltGraph } from './graph-serialization'

interface QuestCanvasProps {
  nodeTemplates: NodeTemplateMeta[]
  /** Called with serialized graph when the user clicks the submit button. */
  onSubmit: (graph: BuiltGraph) => void
  submitting?: boolean
  submitLabel?: string
}

function QuestCanvasInner({ nodeTemplates, onSubmit, submitting, submitLabel = 'Create Quest' }: QuestCanvasProps) {
  const selection = useQuestSelection()
  const graph = useQuestGraphState({
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
  const submission = useQuestSubmission({
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

      <QuestFlowCanvas
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
      </QuestFlowCanvas>

      <InspectorPanel
        selectedNode={selectedNode}
        selectedEdge={selectedEdge}
        onPatchNode={graph.patchNode}
        onDeleteNode={graph.deleteNode}
        onPatchEdge={graph.patchEdge}
        onDeleteEdge={graph.deleteEdge}
      >
        <QuestStatusFooter
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

/** Interactive quest DAG builder: palette, React Flow canvas, inspector. See `AGENTS.md`. */
export function QuestCanvas(props: QuestCanvasProps) {
  return (
    <ReactFlowProvider>
      <QuestCanvasInner {...props} />
    </ReactFlowProvider>
  )
}
