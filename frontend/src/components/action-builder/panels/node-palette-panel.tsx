'use client'

import { useEffect, useRef } from 'react'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import { ScrollArea } from '@/components/ui/scroll-area'
import { CATEGORY_COLOR, CATEGORY_ORDER, PREVIEW_NOT_ENABLED_LABEL, type NodeCategory } from '../node-catalog'
import type { PaletteEntry } from '../node-palette'

interface NodePalettePanelProps {
  groupedByCategory: Map<NodeCategory, PaletteEntry[]>
  filter: string
  onFilterChange: (filter: string) => void
  onAddNode: (entry: PaletteEntry) => void
  onDragStart: (event: React.DragEvent, type: string, templateId?: string) => void
  /** Currently keyboard-highlighted row (reference-equal to an entry in `groupedByCategory`), or `undefined` for none. */
  activeEntry: PaletteEntry | undefined
  onMoveActive: (delta: 1 | -1) => void
  onSetActiveEntry: (entry: PaletteEntry) => void
}

const rowKey = (entry: PaletteEntry) => `${entry.type}:${entry.templateId ?? 'builtin'}`

/** Tooltip text; preview rows say up front that they cannot run yet. */
const rowTitle = (entry: PaletteEntry) =>
  entry.status === 'preview' ? `${PREVIEW_NOT_ENABLED_LABEL}. ${entry.description}` : entry.description

/** Left column: searchable, category-grouped, keyboard-navigable list of draggable node types. */
export function NodePalettePanel({
  groupedByCategory,
  filter,
  onFilterChange,
  onAddNode,
  onDragStart,
  activeEntry,
  onMoveActive,
  onSetActiveEntry,
}: NodePalettePanelProps) {
  const activeRowRef = useRef<HTMLButtonElement>(null)

  useEffect(() => {
    activeRowRef.current?.scrollIntoView({ block: 'nearest' })
  }, [activeEntry])

  const handleSearchKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      onMoveActive(1)
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      onMoveActive(-1)
    } else if (event.key === 'Enter') {
      event.preventDefault()
      if (activeEntry) onAddNode(activeEntry)
    } else if (event.key === 'Escape' && filter) {
      event.preventDefault()
      onFilterChange('')
    }
  }

  return (
    <div className="flex h-full w-56 shrink-0 flex-col overflow-hidden rounded-md border bg-card">
      <div className="border-b p-2">
        <Input
          value={filter}
          onChange={(e) => onFilterChange(e.target.value)}
          onKeyDown={handleSearchKeyDown}
          placeholder="Search nodes…"
          className="h-8 text-xs"
          role="combobox"
          aria-expanded
          aria-controls="action-node-palette-listbox"
          aria-activedescendant={activeEntry ? `palette-row-${rowKey(activeEntry)}` : undefined}
        />
      </div>
      <ScrollArea className="min-h-0 flex-1">
        <div id="action-node-palette-listbox" role="listbox" className="space-y-3 p-2">
          {CATEGORY_ORDER.map((cat) => {
            const items = groupedByCategory.get(cat)
            if (!items || items.length === 0) return null
            return (
              <div key={cat}>
                <div className="mb-1 flex items-center gap-1.5">
                  <span className={`h-2 w-2 rounded-full ${CATEGORY_COLOR[cat].dot}`} />
                  <span className="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                    {cat}
                  </span>
                </div>
                <div className="space-y-1">
                  {items.map((entry) => {
                    const isActive = entry === activeEntry
                    const isPreview = entry.status === 'preview'
                    return (
                      <button
                        key={rowKey(entry)}
                        id={`palette-row-${rowKey(entry)}`}
                        ref={isActive ? activeRowRef : undefined}
                        type="button"
                        role="option"
                        aria-selected={isActive}
                        draggable
                        onDragStart={(e) => onDragStart(e, entry.type, entry.templateId)}
                        onClick={() => onAddNode(entry)}
                        onMouseEnter={() => onSetActiveEntry(entry)}
                        title={rowTitle(entry)}
                        className={`flex w-full cursor-grab items-center justify-between rounded border border-l-2 bg-background px-2 py-1 text-left text-xs transition-colors hover:bg-accent active:cursor-grabbing ${CATEGORY_COLOR[cat].border} ${
                          isActive ? 'bg-accent ring-1 ring-primary/50' : ''
                        } ${isPreview ? 'border-dashed text-muted-foreground' : ''}`}
                      >
                        <span className="truncate">{entry.label}</span>
                        {isPreview && (
                          <Badge
                            variant="outline"
                            aria-label={PREVIEW_NOT_ENABLED_LABEL}
                            className="ml-1 shrink-0 border-amber-500 px-1 py-0 text-[8px] text-amber-700 dark:text-amber-400"
                          >
                            preview
                          </Badge>
                        )}
                        {entry.templateId && (
                          <Badge variant="outline" className="ml-1 shrink-0 text-[8px] px-1 py-0">
                            tpl
                          </Badge>
                        )}
                      </button>
                    )
                  })}
                </div>
              </div>
            )
          })}
          {groupedByCategory.size === 0 && (
            <p className="px-1 text-xs text-muted-foreground">No matching nodes.</p>
          )}
        </div>
      </ScrollArea>
    </div>
  )
}
