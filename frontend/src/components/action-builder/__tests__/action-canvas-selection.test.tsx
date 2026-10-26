import { fireEvent, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import {
  addNodeFromPalette,
  clickFlowNode,
  clickToolbarButton,
  graphCounts,
  isNodeSelected,
  pressCanvasShortcut,
  pressMultiSelectModifier,
  renderActionCanvas,
  toolbarButton,
} from '@/test/action-canvas-harness'

/**
 * C2 regression: the canvas used to overwrite every node's `.selected` from its own
 * `selectedNodeIds` set on the way into `<ReactFlow>`, while that set was fed only by
 * `onSelectionChange` reading back the same prop. A closed loop seeded at `[]`, so no
 * node was ever selected, Copy/Paste were permanently disabled, and the `ring-primary`
 * selected style never rendered. See `../AGENTS.md` §selection.
 */
describe('ActionCanvas selection', () => {
  it('marks a clicked node selected in React Flow', () => {
    renderActionCanvas()
    addNodeFromPalette('Create Holon')

    clickFlowNode('Create Holon')

    expect(isNodeSelected('Create Holon')).toBe(true)
  })

  it('opens the node inspector on single-select', () => {
    renderActionCanvas()
    addNodeFromPalette('Create Holon')

    clickFlowNode('Create Holon')

    expect(screen.getByText('Node Config')).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: 'Name' })).toHaveValue('Create Holon')
    expect(screen.getByRole('textbox', { name: 'Config (JSON)' })).toBeEnabled()
  })

  it('edits the selected node through its named inspector controls', () => {
    renderActionCanvas()
    addNodeFromPalette('Create Holon')
    clickFlowNode('Create Holon')

    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), { target: { value: 'Reviewed request' } })
    const config = '{"name":"Reviewed request"}'
    fireEvent.change(screen.getByRole('textbox', { name: 'Config (JSON)' }), { target: { value: config } })
    addNodeFromPalette('Get Holon')
    clickFlowNode('Get Holon')
    clickFlowNode('Reviewed request')

    expect(isNodeSelected('Reviewed request')).toBe(true)
    expect(screen.getByRole('textbox', { name: 'Name' })).toHaveValue('Reviewed request')
    expect(screen.getByRole('textbox', { name: 'Config (JSON)' })).toHaveValue(config)
  })

  it('enables Copy once a node is selected', () => {
    renderActionCanvas()
    addNodeFromPalette('Create Holon')
    expect(toolbarButton('Copy selection')).toBeDisabled()

    clickFlowNode('Create Holon')

    expect(toolbarButton('Copy selection')).not.toBeDisabled()
  })

  it('accumulates a multi-node selection with the modifier held', () => {
    renderActionCanvas()
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
    renderActionCanvas()
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
    renderActionCanvas()
    addNodeFromPalette('Create Holon')
    clickFlowNode('Create Holon')

    pressCanvasShortcut('c')
    pressCanvasShortcut('v')

    expect(graphCounts().nodes).toBe(2)
  })

  it('pastes every node of a multi-node selection', () => {
    renderActionCanvas()
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
