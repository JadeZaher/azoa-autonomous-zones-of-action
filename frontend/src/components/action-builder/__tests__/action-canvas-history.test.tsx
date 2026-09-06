import { describe, expect, it } from 'vitest'
import {
  clickFlowNode,
  graphCounts,
  loadPreset,
  pressCanvasShortcut,
  pressDeleteKey,
  renderActionCanvas,
} from '@/test/action-canvas-harness'

const pressUndo = () => pressCanvasShortcut('z')

/**
 * C3 regression: React Flow's `deleteElements` fires `triggerEdgeChanges` and
 * `triggerNodeChanges` as back-to-back SYNCHRONOUS statements. Both of our change
 * wrappers saw a `'remove'` and both called `pushHistory()`, and because the
 * snapshot refs are synced in a `useEffect` (which cannot run between two
 * synchronous statements) both recorded the identical pre-delete state. One delete,
 * two entries — so the second Ctrl+Z appeared to do nothing.
 * See `../AGENTS.md` §history.
 */
describe('ActionCanvas undo history', () => {
  it('loads a preset with real edges', () => {
    renderActionCanvas()

    loadPreset(/Linear Holon Pipeline/)

    expect(graphCounts()).toEqual({ nodes: 3, edges: 2 })
  })

  it('records exactly one history entry for a node delete that also removes edges', async () => {
    renderActionCanvas()
    loadPreset(/Linear Holon Pipeline/) // history entry #1: the empty graph

    // The middle node of the pipeline — deleting it removes the node AND both edges,
    // which is what drives the two change-handler calls in a single tick.
    clickFlowNode('Interact')
    await pressDeleteKey()
    expect(graphCounts()).toEqual({ nodes: 2, edges: 0 })

    // One undo must fully restore the delete...
    pressUndo()
    expect(graphCounts()).toEqual({ nodes: 3, edges: 2 })

    // ...and the NEXT undo must reach the entry before it (the empty pre-preset graph).
    // A duplicate entry would make this second press a no-op and leave 3 nodes here.
    pressUndo()
    expect(graphCounts()).toEqual({ nodes: 0, edges: 0 })
  })

  it('keeps redo aligned with the coalesced entry', async () => {
    renderActionCanvas()
    loadPreset(/Linear Holon Pipeline/)
    clickFlowNode('Interact')
    await pressDeleteKey()

    pressUndo()
    pressCanvasShortcut('z', { shiftKey: true })

    expect(graphCounts()).toEqual({ nodes: 2, edges: 0 })
  })
})
