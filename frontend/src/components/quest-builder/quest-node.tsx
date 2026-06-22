'use client'

import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { Badge } from '@/components/ui/badge'
import { CATEGORY_COLOR, categoryFor } from './node-catalog'

/** Data carried by every quest node on the canvas. */
export interface QuestNodeData {
  label: string
  nodeType: string
  config: string
  isEntry: boolean
  isTerminal: boolean
  nodeTemplateId?: string
  /** Execution state, only present in the read-only run view. */
  state?: string
  output?: string
  error?: string
  [key: string]: unknown
}

const STATE_RING: Record<string, string> = {
  Running: 'ring-2 ring-yellow-400 animate-pulse',
  Succeeded: 'ring-2 ring-green-500',
  Failed: 'ring-2 ring-red-500',
  Skipped: 'opacity-60',
  Cancelled: 'opacity-50 ring-2 ring-red-300',
}

/**
 * Custom React Flow node rendering a single quest step: a card with a
 * category-colored left border, target handle on top, source handle on bottom.
 */
function QuestNodeInner({ data, selected }: NodeProps) {
  const d = data as QuestNodeData
  const cat = categoryFor(d.nodeType)
  const colors = CATEGORY_COLOR[cat]
  const ring = d.state ? STATE_RING[d.state] ?? '' : ''

  return (
    <div
      className={`min-w-[180px] max-w-[240px] rounded-md border border-l-4 bg-card shadow-sm transition-shadow ${colors.border} ${ring} ${selected ? 'ring-2 ring-primary' : ''}`}
    >
      <Handle type="target" position={Position.Top} className="!h-2.5 !w-2.5 !bg-muted-foreground" />

      <div className="p-2.5">
        <div className="flex items-start justify-between gap-2">
          <span className="text-sm font-medium leading-tight break-words">{d.label}</span>
          <span className={`mt-0.5 h-2 w-2 shrink-0 rounded-full ${colors.dot}`} />
        </div>
        <p className="mt-0.5 font-mono text-[10px] text-muted-foreground break-all">{d.nodeType}</p>

        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          {d.isEntry && <Badge variant="outline" className="text-[9px] px-1 py-0">Entry</Badge>}
          {d.isTerminal && <Badge variant="outline" className="text-[9px] px-1 py-0">Terminal</Badge>}
          {d.state && (
            <Badge className={`text-[9px] px-1 py-0 ${stateBadge(d.state)}`}>{d.state}</Badge>
          )}
        </div>

        {d.error && <p className="mt-1 text-[10px] text-red-600 break-words">{d.error}</p>}
      </div>

      <Handle type="source" position={Position.Bottom} className="!h-2.5 !w-2.5 !bg-muted-foreground" />
    </div>
  )
}

function stateBadge(state: string): string {
  switch (state) {
    case 'Succeeded': return 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300'
    case 'Running': return 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-300'
    case 'Failed': return 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300'
    case 'Cancelled': return 'bg-red-50 text-red-700 dark:bg-red-900/20 dark:text-red-400'
    default: return 'bg-muted text-muted-foreground'
  }
}

export const QuestNode = memo(QuestNodeInner)
