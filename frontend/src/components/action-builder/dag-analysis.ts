/** Pure graph/config analysis primitives backing the DAG pre-check. See `AGENTS.md` §dag-precheck. */

/** One `{"$from": "<path>"}` occurrence found in a node config. */
export interface BindingSite {
  path: string
}

// ─── $from binding-site collection (mirrors ActionDagExecutabilityValidator.CollectBindingSites) ───
// Walks a node's config JSON for `{"$from": "<path>"}` objects at property-value
// positions only; array-element occurrences are skipped (the backend rejects
// those separately at config-validation time, not here).
export function collectBindingSites(configJson: string | undefined): BindingSite[] {
  const sites: BindingSite[] = []
  if (!configJson || !configJson.includes('"$from"')) return sites
  let root: unknown
  try {
    root = JSON.parse(configJson)
  } catch {
    return sites
  }
  walkForBindings(root, sites)
  return sites
}

function walkForBindings(node: unknown, sites: BindingSite[]): void {
  if (Array.isArray(node)) {
    for (const elem of node) walkForBindings(elem, sites)
    return
  }
  if (node && typeof node === 'object') {
    const obj = node as Record<string, unknown>
    const keys = Object.keys(obj)
    for (const key of keys) {
      const child = obj[key]
      if (
        child &&
        typeof child === 'object' &&
        !Array.isArray(child) &&
        Object.keys(child as Record<string, unknown>).length === 1 &&
        typeof (child as Record<string, unknown>).$from === 'string'
      ) {
        sites.push({ path: (child as Record<string, unknown>).$from as string })
      } else {
        walkForBindings(child, sites)
      }
    }
  }
}

// ─── Dominator dataflow (mirrors ActionDagExecutabilityValidator.ComputeDominators) ───
// Computes, per node id, the set of GUARANTEED ancestors (dominators, excluding
// self): nodes present on EVERY path from an entry to it. Reuses the same
// forward-dataflow fixpoint as the backend, over the canvas's Control-only preds.
//
// `hasAnyIncoming` (any edge type, not just Control) is REQUIRED and separate
// from `controlPreds` — mirroring the backend's explicit guard: "we must NOT
// infer entries from zero Control-preds, since a node reached only via a
// Conditional/OnFailure arm also has zero Control-preds, and treating it as a
// root would let it falsely dominate its own Control-successors." A node with
// zero Control-preds but a Conditional/OnFailure predecessor is NOT a true
// entry and must not seed the dataflow as one.
export function computeDominators(
  nodeIds: string[],
  controlPreds: Map<string, string[]>,
  hasAnyIncoming: Set<string>,
): Map<string, Set<string>> {
  const allIds = new Set(nodeIds)
  const entryIds = new Set(nodeIds.filter((id) => !hasAnyIncoming.has(id)))

  // Nodes Control-reachable from a true entry. A node NOT in this set is never
  // guaranteed to run (reached only via a Conditional/OnFailure arm, or
  // unreachable), so it must not appear in ANY node's guaranteed-ancestor set —
  // stripped from every dom set below, after the fixpoint.
  const controlSucc = new Map<string, string[]>(nodeIds.map((id) => [id, []]))
  controlPreds.forEach((srcs, target) => {
    for (const src of srcs) controlSucc.get(src)?.push(target)
  })
  const controlReachable = new Set(entryIds)
  const queue = Array.from(entryIds)
  while (queue.length > 0) {
    const cur = queue.shift()!
    for (const succ of controlSucc.get(cur) ?? []) {
      if (!controlReachable.has(succ)) {
        controlReachable.add(succ)
        queue.push(succ)
      }
    }
  }

  const dom = new Map<string, Set<string>>()
  for (const id of nodeIds) {
    dom.set(id, entryIds.has(id) ? new Set([id]) : new Set(allIds))
  }

  let changed = true
  let guard = 0
  while (changed && guard++ <= nodeIds.length + 2) {
    changed = false
    for (const id of nodeIds) {
      if (entryIds.has(id)) continue
      let intersection: Set<string> | null = null
      for (const p of controlPreds.get(id) ?? []) {
        const domP = dom.get(p)
        if (!domP) continue
        if (intersection === null) {
          intersection = new Set(domP)
        } else {
          for (const x of Array.from(intersection)) {
            if (!domP.has(x)) intersection.delete(x)
          }
        }
      }
      intersection ??= new Set()
      intersection.add(id)

      const cur = dom.get(id)!
      const same = cur.size === intersection.size && Array.from(cur).every((x) => intersection!.has(x))
      if (!same) {
        dom.set(id, intersection)
        changed = true
      }
    }
  }

  for (const id of nodeIds) {
    const domId = dom.get(id)!
    domId.delete(id)
    // Strip anything not Control-reachable from a true entry — never a guaranteed ancestor.
    for (const x of Array.from(domId)) {
      if (!controlReachable.has(x)) domId.delete(x)
    }
  }
  return dom
}

/** True when a node's config string parses as JSON (empty config counts as `{}`). */
export function isValidJsonConfig(config: string | undefined): boolean {
  try {
    JSON.parse(config || '{}')
    return true
  } catch {
    return false
  }
}
