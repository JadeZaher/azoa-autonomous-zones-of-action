/** Client-side mirror of the backend DAG validators, producing the authoring warning list. See `AGENTS.md` §dag-precheck. */

import type { Edge, Node } from '@xyflow/react'
import type { ActionNodeData } from './action-node'
import type { EdgeData } from './edge-model'
import { collectBindingSites, computeDominators } from './dag-analysis'
import { getOutputShapeMirror } from './node-output-schema'

/**
 * One entry in the pre-check list. `nodeIds` are the canvas nodes the warning is
 * "about", used to (a) paint an authoring-warning ring on the offending node and
 * (b) click-to-center from the list. `error: true` = blocks publish/submit on the server.
 */
export interface DagWarning {
  text: string
  error?: boolean
  nodeIds?: string[]
}

export interface DagWarningInput {
  nodes: Node<ActionNodeData>[]
  edges: Edge[]
  conditionalEdgesMissingCondition: Edge[]
  nodesWithInvalidConfig: Node<ActionNodeData>[]
}

/**
 * Mirrors the backend ActionDagValidator rules so the user sees structural
 * problems (missing entry/terminal, orphans, cycles) BEFORE submitting and
 * eating a 400. Not authoritative — the server re-validates — but it stops
 * the common "why won't my action save" guessing game.
 */
export function computeDagWarnings({
  nodes,
  edges,
  conditionalEdgesMissingCondition,
  nodesWithInvalidConfig,
}: DagWarningInput): DagWarning[] {
  const warnings: DagWarning[] = []
  if (nodes.length === 0) return warnings

  const ids = new Set(nodes.map((n) => n.id))
  const incoming = new Map(nodes.map((n) => [n.id, 0]))
  const outgoing = new Map(nodes.map((n) => [n.id, 0]))
  const controlOut = new Map(nodes.map((n) => [n.id, 0]))
  const onFailureOut = new Map(nodes.map((n) => [n.id, 0]))
  const adj = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
  for (const e of edges) {
    if (!ids.has(e.source) || !ids.has(e.target)) continue
    incoming.set(e.target, (incoming.get(e.target) ?? 0) + 1)
    outgoing.set(e.source, (outgoing.get(e.source) ?? 0) + 1)
    // Control fan-out / dominators key on Control ONLY — mirrors the backend
    // validator (ActionDagValidator Control fan-out + ActionDagExecutabilityValidator
    // dominators use EdgeType==Control exclusively). OnFailure fan-out is tracked
    // separately below (its own rule, not part of the Control spine).
    const edgeType = (e.data as EdgeData)?.edgeType
    if (edgeType === 'Control') {
      controlOut.set(e.source, (controlOut.get(e.source) ?? 0) + 1)
    } else if (edgeType === 'OnFailure') {
      onFailureOut.set(e.source, (onFailureOut.get(e.source) ?? 0) + 1)
    }
    adj.get(e.source)!.push(e.target)
  }

  const roots = nodes.filter((n) => (incoming.get(n.id) ?? 0) === 0)
  const markedEntries = nodes.filter((n) => n.data.isEntry)
  const markedTerminals = nodes.filter((n) => n.data.isTerminal)

  if (markedEntries.length === 0) {
    warnings.push({ text: `No node is marked as Entry. Mark the starting node's "Entry" flag in the inspector.` })
  }
  const unmarkedRoots = roots.filter((n) => !n.data.isEntry)
  if (unmarkedRoots.length > 0) {
    warnings.push({
      text: `Orphan (no incoming edge, not Entry): ${unmarkedRoots.map((n) => n.data.label).join(', ')}.`,
      nodeIds: unmarkedRoots.map((n) => n.id),
    })
  }
  if (markedTerminals.length === 0) {
    warnings.push({ text: `No node is marked as Terminal. Mark a leaf node's "Terminal" flag in the inspector.` })
  }
  const terminalNotLeaf = markedTerminals.filter((n) => (outgoing.get(n.id) ?? 0) > 0)
  if (terminalNotLeaf.length > 0) {
    warnings.push({
      text: `Marked Terminal but has outgoing edges: ${terminalNotLeaf.map((n) => n.data.label).join(', ')}.`,
      nodeIds: terminalNotLeaf.map((n) => n.id),
    })
  }

  // Fan-out: >1 outgoing Control edge from any node (ActionDagValidator.cs:184-202).
  // Advisory, not error-level: the backend only promotes this to a hard error
  // under `fanOutAsError` (publish time — ActionManager.PublishAsync); creation
  // and the plain "Validate DAG" action accept it as a warning. Matching that
  // keeps `error: true` meaning "the server will reject this right now."
  const fanOutNodes = nodes.filter((n) => (controlOut.get(n.id) ?? 0) > 1)
  for (const n of fanOutNodes) {
    warnings.push({
      text: `Fan-out on "${n.data.label}" (${controlOut.get(n.id)} outgoing Control edges) — won't publish; durable engine requires a single Control successor.`,
      nodeIds: [n.id],
    })
  }

  // OnFailure fan-out: >1 outgoing OnFailure edge from any node
  // (ActionDagValidator.cs:204-226 — "exactly-one-arm property, V2"). Same
  // fanOutAsError-gated severity as Control fan-out above: advisory here,
  // promoted to a hard error only at publish.
  const onFailureFanOutNodes = nodes.filter((n) => (onFailureOut.get(n.id) ?? 0) > 1)
  for (const n of onFailureFanOutNodes) {
    warnings.push({
      text: `OnFailure fan-out on "${n.data.label}" (${onFailureOut.get(n.id)} outgoing OnFailure edges) — won't publish; at most one OnFailure arm is allowed per node.`,
      nodeIds: [n.id],
    })
  }

  // Conditional edges missing condition text: server hard-rejects these (FR-1c).
  for (const e of conditionalEdgesMissingCondition) {
    const src = nodes.find((n) => n.id === e.source)
    const tgt = nodes.find((n) => n.id === e.target)
    warnings.push({
      text: `Conditional edge from "${src?.data.label ?? e.source}" → "${tgt?.data.label ?? e.target}" has no condition text — server will reject this edge.`,
      error: true,
      nodeIds: [e.source, e.target].filter((id) => ids.has(id)),
    })
  }

  // Nodes with invalid config JSON block the server round-trip (FR-8d).
  for (const n of nodesWithInvalidConfig) {
    warnings.push({
      text: `Node "${n.data.label}" has invalid JSON config — fix before submitting.`,
      error: true,
      nodeIds: [n.id],
    })
  }

  // ── $from binding executability (mirrors ActionDagExecutabilityValidator) ──
  // Best-effort + non-authoritative: the server is the real gate. When unsure
  // (open schema, holon.*, deep paths, malformed config) we SKIP rather than
  // risk a false positive — nodesWithInvalidConfig already flags bad JSON.
  {
    const invalidConfigIds = new Set(nodesWithInvalidConfig.map((n) => n.id))
    const nameById = new Map(nodes.map((n) => [n.id, n.data.label]))
    const idsByName = new Map<string, string[]>()
    for (const n of nodes) {
      const list = idsByName.get(n.data.label) ?? []
      list.push(n.id)
      idsByName.set(n.data.label, list)
    }

    // Duplicate labels: server rejects at publish, and the binding checker below
    // silently skips ambiguous names — so surface it explicitly (error-level).
    // forEach (not for..of) — Map iteration needs downlevelIteration under this tsconfig.
    idsByName.forEach((dupIds, name) => {
      if (dupIds.length > 1) {
        warnings.push({
          text: `Duplicate node name "${name}" (×${dupIds.length}) — bindings are ambiguous and publish will fail. Rename so every node is unique.`,
          error: true,
          nodeIds: dupIds,
        })
      }
    })
    // ALL-edge predecessors: upstream-scope mirror (BuildUpstreamScope walks EVERY
    // incoming edge regardless of type). directPredNames uses this — intentional.
    const preds = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
    adj.forEach((targets, src) => {
      for (const tgt of targets) preds.get(tgt)?.push(src)
    })
    // Control-ONLY predecessors: dominators are computed over the Control spine only
    // (mirrors backend ActionDagExecutabilityValidator.ComputeDominators, which
    // traverses EdgeType==Control exclusively). Conditional/OnFailure edges do NOT
    // contribute a guaranteed-ancestry path.
    const controlPreds = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
    for (const e of edges) {
      if (!ids.has(e.source) || !ids.has(e.target)) continue
      if ((e.data as EdgeData)?.edgeType !== 'Control') continue
      controlPreds.get(e.target)?.push(e.source)
    }
    // True entries = nodes with no incoming edge of ANY type — NOT "zero Control
    // predecessors" (a node reached only via a Conditional/OnFailure arm also has
    // zero Control-preds, and must not be treated as a dataflow root; see
    // computeDominators in dag-analysis.ts and the backend comment it mirrors).
    const hasAnyIncoming = new Set(nodes.filter((n) => (incoming.get(n.id) ?? 0) > 0).map((n) => n.id))
    const dominators = computeDominators(nodes.map((n) => n.id), controlPreds, hasAnyIncoming)

    for (const consumer of nodes) {
      if (invalidConfigIds.has(consumer.id)) continue // already flagged; don't double-report
      const sites = collectBindingSites(consumer.data.config)
      if (sites.length === 0) continue

      const directPredNames = new Set(
        (preds.get(consumer.id) ?? []).map((id) => nameById.get(id)).filter((x): x is string => !!x),
      )
      const domNames = new Set(
        Array.from(dominators.get(consumer.id) ?? []).map((id) => nameById.get(id)).filter((x): x is string => !!x),
      )

      for (const { path } of sites) {
        const segments = path.split('.')
        if (segments.length < 2) continue // malformed — grammar errors are the config validator's job
        const [root, refName, ...rest] = segments

        if (root === 'holon') continue // dynamic state — skip

        if (root !== 'upstream' && root !== 'run') {
          warnings.push({
            text: `Node "${consumer.data.label}": "${path}" has an unrecognized binding root "${root}" (expected "upstream", "run", or "holon").`,
            error: true,
            nodeIds: [consumer.id],
          })
          continue
        }

        // Ambiguous name (duplicate labels) — skip rather than risk a false positive.
        const candidateIds = idsByName.get(refName)
        if (!candidateIds || candidateIds.length === 0) continue // unresolvable name — skip (server will catch it)
        if (candidateIds.length > 1) continue

        const refNode = nodes.find((n) => n.id === candidateIds[0])
        if (!refNode) continue

        if (root === 'upstream') {
          if (!directPredNames.has(refName)) {
            warnings.push({
              text: `Node "${consumer.data.label}": upstream binding "${path}" references "${refName}" which is not a direct predecessor.`,
              error: true,
              nodeIds: [consumer.id],
            })
            continue
          }
        } else {
          // root === 'run'
          if (!domNames.has(refName)) {
            warnings.push({
              text: `Node "${consumer.data.label}": run binding "${path}" references "${refName}", which is not guaranteed to run before it (not on every path) — its output may be absent.`,
              error: true,
              nodeIds: [consumer.id],
            })
            continue
          }
        }

        // ── Field presence (best-effort; skip on open schema or ambiguity) ──
        const shape = getOutputShapeMirror(refNode.data.nodeType)
        if (shape.open) continue // opaque output — ancestry alone gates it

        if (rest.length === 0) continue // no field segment to check — nothing more to say

        const fieldSeg = rest[0]
        const matchedKey = Object.keys(shape.fields).find(
          (f) => f.toLowerCase() === fieldSeg.toLowerCase(),
        )
        if (!matchedKey) {
          warnings.push({
            text: `Node "${consumer.data.label}": "${path}" — field "${fieldSeg}" is not in the output of "${refName}".`,
            error: true,
            nodeIds: [consumer.id],
          })
          continue
        }
        // Deep paths into a "deep" (Object/Array) field are admitted with no
        // further checks (unknown nested shape); scalar fields with extra
        // segments are also skipped client-side (never false-positive).
      }
    }
  }

  // Reachability + cycle detection via BFS from marked entries.
  const reachable = new Set<string>()
  const queue = markedEntries.map((n) => n.id)
  queue.forEach((id) => reachable.add(id))
  while (queue.length > 0) {
    const cur = queue.shift()!
    for (const next of adj.get(cur) ?? []) {
      if (!reachable.has(next)) {
        reachable.add(next)
        queue.push(next)
      }
    }
  }
  if (markedEntries.length > 0) {
    const unreachable = nodes.filter((n) => !reachable.has(n.id))
    if (unreachable.length > 0) {
      warnings.push({
        text: `Not reachable from an Entry node: ${unreachable.map((n) => n.data.label).join(', ')}.`,
        nodeIds: unreachable.map((n) => n.id),
      })
    }
  }

  // Kahn cycle detection.
  const indeg = new Map(nodes.map((n) => [n.id, incoming.get(n.id) ?? 0]))
  const kq = nodes.filter((n) => (indeg.get(n.id) ?? 0) === 0).map((n) => n.id)
  let visited = 0
  while (kq.length > 0) {
    const cur = kq.shift()!
    visited++
    for (const next of adj.get(cur) ?? []) {
      indeg.set(next, (indeg.get(next) ?? 0) - 1)
      if ((indeg.get(next) ?? 0) === 0) kq.push(next)
    }
  }
  if (visited !== nodes.length) {
    warnings.push({ text: 'Graph contains a cycle — actions must be acyclic (DAG).' })
  }

  // Skip-cascade advisory: remind authors that a failed/skipped node cascades
  // through the entire downstream Control chain, not just one hop.
  if (edges.some((e) => (e.data as EdgeData)?.edgeType === 'Control')) {
    warnings.push({
      text: 'Skip cascades: if any node fails or is skipped, ALL downstream Control-chain nodes are also skipped (not just the next hop).',
    })
  }

  return warnings
}
