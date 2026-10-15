import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { Edge, Node } from '@xyflow/react'
import type { PaletteEntry } from '../../node-palette'
import type { ActionNodeData } from '../../action-node'
import type { SubgraphClipboard } from '../use-subgraph-clipboard'
import { useActionGraphState } from '../use-action-graph-state'

const HOLON_CREATE: PaletteEntry = {
  type: 'HolonCreate',
  category: 'Holon',
  label: 'Create Holon',
  description: 'Create a new holon',
  defaultConfig: '{}',
}

function renderGraphState() {
  const setSelectedNodeId = vi.fn()
  const setSelectedEdgeId = vi.fn()
  const hook = renderHook(() => useActionGraphState({ setSelectedNodeId, setSelectedEdgeId }))
  return { ...hook, setSelectedNodeId, setSelectedEdgeId }
}

/** Two nodes joined by one Control edge, built through the hook's own public mutations. */
function buildTwoNodeGraph(result: { current: ReturnType<typeof useActionGraphState> }) {
  act(() => result.current.addNode(HOLON_CREATE))
  act(() => result.current.addNode(HOLON_CREATE))
  const [source, target] = result.current.nodes
  act(() =>
    result.current.connectNodes({
      source: source.id,
      target: target.id,
      sourceHandle: null,
      targetHandle: null,
    }),
  )
  return { source, target }
}

function clipboardOf(node: Node<ActionNodeData>): SubgraphClipboard {
  return {
    nodes: [{ sourceId: node.id, position: { ...node.position }, data: { ...node.data } }],
    edges: [],
  }
}

describe('useActionGraphState history', () => {
  it('records one entry when a delete drives both change streams in a single tick', () => {
    const { result } = renderGraphState()
    buildTwoNodeGraph(result)
    const edgeId = result.current.edges[0].id
    const doomedNodeId = result.current.nodes[1].id

    // Exactly what `deleteElements` does: edge removals, then node removals, with no
    // commit in between. Both wrappers see a 'remove' and both call pushHistory.
    act(() => {
      result.current.onEdgesChange([{ id: edgeId, type: 'remove' }])
      result.current.onNodesChange([{ id: doomedNodeId, type: 'remove' }])
    })
    expect(result.current.nodes).toHaveLength(1)
    expect(result.current.edges).toHaveLength(0)

    // Walking the whole stack back to empty is what proves the COUNT: three
    // mutations built the graph, so a fourth undo must land on the empty canvas.
    // A duplicate entry would leave one more undo owing.
    act(() => result.current.undo())
    expect(result.current.nodes).toHaveLength(2)
    expect(result.current.edges).toHaveLength(1)

    act(() => result.current.undo())
    act(() => result.current.undo())
    act(() => result.current.undo())
    expect(result.current.nodes).toHaveLength(0)
    expect(result.current.canUndo).toBe(false)
  })

  it('still records a history entry when only an edge is removed', () => {
    const { result } = renderGraphState()
    buildTwoNodeGraph(result)
    const edgeId = result.current.edges[0].id

    act(() => result.current.onEdgesChange([{ id: edgeId, type: 'remove' }]))
    expect(result.current.edges).toHaveLength(0)

    act(() => result.current.undo())
    expect(result.current.edges).toHaveLength(1)
    expect(result.current.nodes).toHaveLength(2)
  })

  it('does not snapshot non-destructive changes', () => {
    const { result } = renderGraphState()
    act(() => result.current.addNode(HOLON_CREATE))
    const nodeId = result.current.nodes[0].id

    act(() =>
      result.current.onNodesChange([
        { id: nodeId, type: 'position', position: { x: 5, y: 5 }, dragging: true },
      ]),
    )

    act(() => result.current.undo())
    expect(result.current.canUndo).toBe(false)
    expect(result.current.nodes).toHaveLength(0)
  })
})

describe('useActionGraphState paste', () => {
  it('offsets each successive paste of the same clipboard', () => {
    const { result } = renderGraphState()
    act(() => result.current.addNode(HOLON_CREATE))
    const original = result.current.nodes[0]
    const clipboard = clipboardOf(original)

    act(() => result.current.pasteSubgraph(clipboard))
    act(() => result.current.pasteSubgraph(clipboard))

    const [, firstPaste, secondPaste] = result.current.nodes
    expect(firstPaste.position).not.toEqual(original.position)
    // C8: the offset used to be computed from the clipboard, which never moves, so a
    // second Ctrl+V landed exactly on top of the first copy.
    expect(secondPaste.position).not.toEqual(firstPaste.position)
  })

  it('keeps label uniqueness across repeated pastes', () => {
    const { result } = renderGraphState()
    act(() => result.current.addNode(HOLON_CREATE))
    const clipboard = clipboardOf(result.current.nodes[0])

    act(() => result.current.pasteSubgraph(clipboard))
    act(() => result.current.pasteSubgraph(clipboard))

    expect(result.current.nodes.map((n) => n.data.label)).toEqual([
      'Create Holon',
      'Create Holon 2',
      'Create Holon 3',
    ])
  })

  it('restarts the offset when a fresh clipboard is pasted', () => {
    const { result } = renderGraphState()
    act(() => result.current.addNode(HOLON_CREATE))
    const original = result.current.nodes[0]

    act(() => result.current.pasteSubgraph(clipboardOf(original)))
    const firstPaste = result.current.nodes[1]
    act(() => result.current.pasteSubgraph(clipboardOf(original)))
    const secondPaste = result.current.nodes[2]

    expect(secondPaste.position).toEqual(firstPaste.position)
  })

  it('remaps internal edges onto the pasted copies', () => {
    const { result } = renderGraphState()
    buildTwoNodeGraph(result)
    const [first, second] = result.current.nodes
    const originalEdge = result.current.edges[0] as Edge

    act(() =>
      result.current.pasteSubgraph({
        nodes: [
          { sourceId: first.id, position: { ...first.position }, data: { ...first.data } },
          { sourceId: second.id, position: { ...second.position }, data: { ...second.data } },
        ],
        edges: [{ sourceId: first.id, targetId: second.id, data: { edgeType: 'Control' } }],
      }),
    )

    expect(result.current.edges).toHaveLength(2)
    const pastedEdge = result.current.edges.find((e) => e.id !== originalEdge.id)!
    expect(pastedEdge.source).not.toBe(first.id)
    expect(pastedEdge.target).not.toBe(second.id)
    // Both endpoints must land on nodes that actually exist, or the paste dangles.
    const ids = result.current.nodes.map((n) => n.id)
    expect(ids).toContain(pastedEdge.source)
    expect(ids).toContain(pastedEdge.target)
  })

  it('strips entry/terminal flags from a pasted copy', () => {
    const { result } = renderGraphState()
    act(() => result.current.addNode(HOLON_CREATE))
    const original = result.current.nodes[0]
    expect(original.data.isEntry).toBe(true)

    act(() => result.current.pasteSubgraph(clipboardOf(original)))

    expect(result.current.nodes[1].data.isEntry).toBe(false)
    expect(result.current.nodes[1].data.isTerminal).toBe(false)
  })
})
