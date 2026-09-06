import { describe, expect, it } from 'vitest'
import { renderActionCanvas, addNodeFromPalette, graphCounts } from '@/test/action-canvas-harness'

const idle = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * C1 regression: `onSelectionChange` used to be an inline arrow re-created on every
 * render, and `handleSelectionChange` always produced a brand-new array. React Flow
 * lists `onSelectionChange` in `SelectionListenerInner`'s effect deps, and
 * `<ReactFlow>` is `fixedForwardRef` (NOT memo), so it re-rendered with its parent:
 * render → new arrow → deps changed → effect → setState(new []) → render → ∞.
 */
describe('ActionCanvas render stability', () => {
  it('settles after mount instead of looping', () => {
    const canvas = renderActionCanvas()

    // A runaway loop trips React's "Maximum update depth exceeded" before this line;
    // a merely-wasteful one shows up as a commit count in the hundreds.
    expect(canvas.commitCount()).toBeLessThan(10)
  })

  it('does not re-render on an idle tick once settled', async () => {
    const canvas = renderActionCanvas()

    // React Flow finishes its own viewport/fitView pass on a deferred tick, which is
    // one legitimate extra commit. Let that land, THEN require complete quiescence —
    // the loop this guards against never stops, so it cannot hide in the settling window.
    await idle(50)
    canvas.resetCommitCount()
    await idle(100)

    expect(canvas.commitCount()).toBe(0)
  })

  it('costs a bounded number of commits per node added', () => {
    const canvas = renderActionCanvas()
    canvas.resetCommitCount()

    addNodeFromPalette('Create Holon')

    expect(graphCounts()).toEqual({ nodes: 1, edges: 0 })
    expect(canvas.commitCount()).toBeLessThan(10)
  })
})
