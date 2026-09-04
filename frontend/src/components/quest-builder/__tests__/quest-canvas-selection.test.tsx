import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import {
  addNodeFromPalette,
  clickFlowNode,
  clickToolbarButton,
  graphCounts,
  isNodeSelected,
  pressCanvasShortcut,
  pressMultiSelectModifier,
  renderQuestCanvas,
  toolbarButton,
} from '@/test/quest-canvas-harness'

/**
 * C2 regression: the canvas used to overwrite every node's `.selected` from its own
 * `selectedNodeIds` set on the way into `<ReactFlow>`, while that set was fed only by
 * `onSelectionChange` reading back the same prop. A closed loop seeded at `[]`, so no
 * node was ever selected, Copy/Paste were permanently disabled, and the `ring-primary`
 * selected style never rendered. See `../AGENTS.md` §selection.
 */
describe('QuestCanvas selection', () => {
  it('marks a clicked node selected in React Flow', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')

    clickFlowNode('Create Holon')

    expect(isNodeSelected('Create Holon')).toBe(true)
  })

  it('opens the node inspector on single-select', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')

    clickFlowNode('Create Holon')

    expect(screen.getByText('Node Config')).toBeInTheDocument()
  })

  it('enables Copy once a node is selected', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')
    expect(toolbarButton('Copy selection')).toBeDisabled()

    clickFlowNode('Create Holon')

    expect(toolbarButton('Copy selection')).not.toBeDisabled()
  })

  it('accumulates a multi-node selection with the modifier held', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')
    addNodeFromPalette('Get Holon')
    clickFlowNode('Create Holon')

    pressMultiSelectModifier(true)
    clickFlowNode('Get Holon')
    pressMultiSelectModifier(false)

    expect(isNodeSelected('Create Holon')).toBe(true)
    expect(isNodeSelected('Get Holon')).toBe(true)
  })

  it('round-trips copy → paste through the toolbar', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')
    clickFlowNode('Create Holon')

    clickToolbarButton('Copy selection')
    expect(toolbarButton('Paste')).not.toBeDisabled()
    clickToolbarButton('Paste')

    expect(graphCounts().nodes).toBe(2)
    // Labels are the `$from` binding key, so the pasted copy must be auto-uniquified.
    expect(screen.getByText('Create Holon 2')).toBeInTheDocument()
  })

  it('round-trips copy → paste through Ctrl+C / Ctrl+V', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')
    clickFlowNode('Create Holon')

    pressCanvasShortcut('c')
    pressCanvasShortcut('v')

    expect(graphCounts().nodes).toBe(2)
  })

  it('pastes every node of a multi-node selection', () => {
    renderQuestCanvas()
    addNodeFromPalette('Create Holon')
    addNodeFromPalette('Get Holon')
    clickFlowNode('Create Holon')
    pressMultiSelectModifier(true)
    clickFlowNode('Get Holon')
    pressMultiSelectModifier(false)

    clickToolbarButton('Copy selection')
    clickToolbarButton('Paste')

    expect(graphCounts().nodes).toBe(4)
  })
})
