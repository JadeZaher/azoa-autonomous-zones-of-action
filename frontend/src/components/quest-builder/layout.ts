/**
 * Dependency-free layered DAG layout.
 *
 * Assigns each node a layer = longest path from any entry, then spreads nodes
 * horizontally within their layer. Good enough for the modestly-sized quest
 * graphs we render, and avoids pulling in dagre/elkjs.
 */

import type { Edge, Node } from '@xyflow/react'

const LAYER_GAP = 140
const NODE_GAP = 280
const X_ORIGIN = 60
const Y_ORIGIN = 40

export function layoutGraph(nodes: Node[], edges: Edge[]): Node[] {
  if (nodes.length === 0) return nodes

  const incoming = new Map<string, string[]>()
  const outgoing = new Map<string, string[]>()
  for (const n of nodes) {
    incoming.set(n.id, [])
    outgoing.set(n.id, [])
  }
  for (const e of edges) {
    if (outgoing.has(e.source)) outgoing.get(e.source)!.push(e.target)
    if (incoming.has(e.target)) incoming.get(e.target)!.push(e.source)
  }

  // Longest-path layering via Kahn-style relaxation (cycle-safe: capped).
  const layer = new Map<string, number>()
  for (const n of nodes) layer.set(n.id, 0)
  const maxIter = nodes.length + 1
  for (let i = 0; i < maxIter; i++) {
    let changed = false
    for (const e of edges) {
      const sl = layer.get(e.source) ?? 0
      const tl = layer.get(e.target) ?? 0
      if (tl < sl + 1) {
        layer.set(e.target, sl + 1)
        changed = true
      }
    }
    if (!changed) break
  }

  // Group by layer, preserving original node order within each layer.
  const byLayer = new Map<number, string[]>()
  for (const n of nodes) {
    const l = layer.get(n.id) ?? 0
    if (!byLayer.has(l)) byLayer.set(l, [])
    byLayer.get(l)!.push(n.id)
  }

  const pos = new Map<string, { x: number; y: number }>()
  byLayer.forEach((ids, l) => {
    ids.forEach((id, idx) => {
      pos.set(id, { x: X_ORIGIN + idx * NODE_GAP, y: Y_ORIGIN + l * LAYER_GAP })
    })
  })

  return nodes.map((n) => ({ ...n, position: pos.get(n.id) ?? n.position }))
}
