'use client'

import { useCallback, useMemo, useState } from 'react'
import { CATEGORY_ORDER, type NodeCategory } from '../node-catalog'
import { buildPalette, type NodeTemplateMeta, type PaletteEntry } from '../node-palette'

/**
 * Palette entries built from catalog + API templates, filtered and grouped by
 * category, plus a flat (visual-order) list and an active-row index for
 * keyboard navigation. See `../AGENTS.md` §palette-search.
 */
export function useNodePalette(nodeTemplates: NodeTemplateMeta[]) {
  const [filter, setFilterState] = useState('')
  const [activeIndex, setActiveIndex] = useState(-1)

  // Reset the active row in the SAME event as the keystroke, not a reactive effect
  // keyed on `filter` — every path that changes `filter` goes through this one
  // setter, so there's no need for a second render pass just to notice it changed.
  // Speculatively landing on index 0 is safe even before `flatEntries` recomputes:
  // `activeEntry` below falls back to `undefined` once flatEntries is shorter.
  const setFilter = useCallback((value: string) => {
    setFilterState(value)
    setActiveIndex(0)
  }, [])

  const entries = useMemo(() => buildPalette(nodeTemplates), [nodeTemplates])

  const groupedByCategory = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    const map = new Map<NodeCategory, PaletteEntry[]>()
    for (const entry of entries) {
      // Matches name, node type, description, OR category — a search for "economic"
      // should surface the whole Economic group, not just entries whose own text
      // happens to contain it.
      const haystack = `${entry.label} ${entry.type} ${entry.description} ${entry.category}`.toLowerCase()
      if (needle && !haystack.includes(needle)) continue
      if (!map.has(entry.category)) map.set(entry.category, [])
      map.get(entry.category)!.push(entry)
    }
    return map
  }, [entries, filter])

  // Same order the panel renders in (CATEGORY_ORDER, then within-category insertion
  // order) so ArrowUp/ArrowDown steps to the visually-adjacent row every time.
  const flatEntries = useMemo(() => {
    const flat: PaletteEntry[] = []
    for (const cat of CATEGORY_ORDER) {
      const items = groupedByCategory.get(cat)
      if (items) flat.push(...items)
    }
    return flat
  }, [groupedByCategory])

  const moveActive = useCallback(
    (delta: 1 | -1) => {
      if (flatEntries.length === 0) return
      setActiveIndex((prev) => {
        const base = prev < 0 ? (delta > 0 ? -1 : 0) : prev
        return (base + delta + flatEntries.length) % flatEntries.length
      })
    },
    [flatEntries.length],
  )

  const setActiveEntry = useCallback(
    (entry: PaletteEntry) => {
      const index = flatEntries.indexOf(entry)
      if (index >= 0) setActiveIndex(index)
    },
    [flatEntries],
  )

  const activeEntry = activeIndex >= 0 ? flatEntries[activeIndex] : undefined

  return { entries, filter, setFilter, groupedByCategory, moveActive, activeEntry, setActiveEntry }
}
