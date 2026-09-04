/** Edge data shape and visual styling for canvas edges. See `AGENTS.md` §edge-model. */

import type { Edge } from '@xyflow/react'

/** Data stored on each edge in the canvas. */
export interface EdgeData {
  // Mirrors backend QuestEdgeType (Control | Conditional | OnFailure). OnFailure is
  // a first-class error-routing edge; it is NOT a Control edge (see dominator/fan-out
  // logic in dag-warnings.ts, which keys on edgeType === 'Control' exactly).
  edgeType?: 'Control' | 'Conditional' | 'OnFailure'
  condition?: string
}

/** Selectable edge types, in inspector display order. */
export const EDGE_TYPE_OPTIONS = ['Control', 'Conditional', 'OnFailure'] as const

/** Visual style for an edge based on its type. */
export function edgeStyle(type: string | undefined): Pick<Edge, 'animated' | 'style' | 'label'> {
  if (type === 'Conditional') {
    return { animated: true, style: { stroke: '#f59e0b', strokeDasharray: '5 5' } }
  }
  if (type === 'OnFailure') {
    return { animated: false, style: { stroke: '#ef4444', strokeDasharray: '4 4' } }
  }
  return { animated: false, style: { stroke: '#94a3b8' } }
}
