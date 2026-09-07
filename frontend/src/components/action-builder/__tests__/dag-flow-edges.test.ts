import { describe, expect, it, vi } from 'vitest'
import { toFlowEdge } from '../dag-flow'

// C11: `edgeStyle`'s declared return type includes `label`. Pretend a future stroke
// table starts setting one — the read-only DAG render must still win, because its
// label carries the actual condition text. (`vi.mock` is hoisted above the import.)
vi.mock('../edge-model', () => ({
  EDGE_TYPE_OPTIONS: ['Control', 'Conditional', 'OnFailure'] as const,
  edgeStyle: () => ({ animated: false, style: { stroke: '#94a3b8' }, label: 'from-style-table' }),
}))

describe('toFlowEdge', () => {
  it('keeps the condition text as a Conditional edge label', () => {
    const edge = toFlowEdge({
      id: 'e1',
      sourceNodeId: 'a',
      targetNodeId: 'b',
      edgeType: 'Conditional',
      condition: 'output.ok == true',
    })

    expect(edge.label).toBe('output.ok == true')
  })

  it('falls back to "if" for a Conditional edge with no condition text', () => {
    const edge = toFlowEdge({ id: 'e1', sourceNodeId: 'a', targetNodeId: 'b', edgeType: 'Conditional' })

    expect(edge.label).toBe('if')
  })

  it('labels OnFailure edges', () => {
    const edge = toFlowEdge({ id: 'e1', sourceNodeId: 'a', targetNodeId: 'b', edgeType: 'OnFailure' })

    expect(edge.label).toBe('on failure')
  })

  it('leaves plain Control edges unlabelled', () => {
    const edge = toFlowEdge({ id: 'e1', sourceNodeId: 'a', targetNodeId: 'b', edgeType: 'Control' })

    expect(edge.label).toBeUndefined()
  })

  it('still takes stroke styling from the shared edge-model table', () => {
    const edge = toFlowEdge({ id: 'e1', sourceNodeId: 'a', targetNodeId: 'b', edgeType: 'Control' })

    expect(edge.style).toEqual({ stroke: '#94a3b8' })
  })
})
