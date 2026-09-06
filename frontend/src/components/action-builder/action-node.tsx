'use client'

import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { Badge } from '@/components/ui/badge'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { CATEGORY_COLOR, categoryFor } from './node-catalog'
import type { DagWarning } from './dag-warnings'

/** Data carried by every action node on the canvas. */
export interface ActionNodeData {
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
  /** Authoring-time: DAG pre-check findings that name this node (builder only). See `dag-warnings.ts`. */
  warnings?: DagWarning[]
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
 * Custom React Flow node rendering a single action step: a card with a
 * category-colored left border, target handle on top, source handle on bottom.
 */
function ActionNodeInner({ data, selected }: NodeProps) {
  const d = data as ActionNodeData
  const cat = categoryFor(d.nodeType)
  const colors = CATEGORY_COLOR[cat]
  const ring = d.state ? STATE_RING[d.state] ?? '' : ''
  const warnings = d.warnings ?? []
  const hasError = warnings.some((w) => w.error)
  const hasAdvisory = warnings.length > 0 && !hasError
  // Authoring-warning ring — distinct from the runtime STATE_RING and only shown
  // when there's no runtime state to report (run view takes visual precedence).
  // error:true (red) vs advisory (amber) mirrors §dag-precheck's severity split —
  // an advisory fan-out finding must not read as urgently as a hard-reject.
  const warnRing = d.state
    ? ''
    : hasError
    ? 'ring-2 ring-red-500 ring-offset-1 ring-offset-background'
    : hasAdvisory
    ? 'ring-2 ring-amber-400 ring-offset-1 ring-offset-background'
    : ''

  return (
    <div
      className={`min-w-[180px] max-w-[240px] rounded-md border border-l-4 bg-card shadow-sm transition-shadow ${colors.border} ${ring} ${warnRing} ${selected ? 'ring-2 ring-primary' : ''}`}
    >
      <Handle type="target" position={Position.Top} className="!h-2.5 !w-2.5 !bg-muted-foreground" />

      <div className="p-2.5">
        <div className="flex items-start justify-between gap-2">
          <span className="text-sm font-medium leading-tight break-words">{d.label}</span>
          <div className="flex shrink-0 items-center gap-1">
            {warnings.length > 0 && <NodeWarningIndicator warnings={warnings} hasError={hasError} />}
            <span className={`mt-0.5 h-2 w-2 rounded-full ${colors.dot}`} />
          </div>
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

/**
 * Small badge on the node card surfacing its DAG pre-check findings without forcing
 * the author to scroll to the footer list. CLICK-ONLY, deliberately — see
 * `./AGENTS.md` §warning-badge for why hover-to-open cannot work here.
 * `nodrag`/`nopan` so interacting with it doesn't start a canvas drag/pan.
 */
function NodeWarningIndicator({ warnings, hasError }: { warnings: DagWarning[]; hasError: boolean }) {
  const findingsLabel = `${warnings.length} pre-check ${warnings.length === 1 ? 'finding' : 'findings'}`

  return (
    <Popover>
      <PopoverTrigger
        aria-label={findingsLabel}
        className={`nodrag nopan flex h-4 min-w-4 cursor-pointer items-center justify-center rounded-full px-1 text-[9px] font-bold leading-none ${
          hasError ? 'bg-red-500 text-white' : 'bg-amber-400 text-amber-950'
        }`}
      >
        {hasError ? '✕' : '⚠'}
      </PopoverTrigger>
      <PopoverContent side="right" className="w-64 p-2 text-[11px]">
        <ul className="space-y-1">
          {warnings.map((w, i) => (
            <li key={i} className={w.error ? 'text-red-600 dark:text-red-400' : 'text-amber-700 dark:text-amber-400'}>
              {w.text}
            </li>
          ))}
        </ul>
      </PopoverContent>
    </Popover>
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

export const ActionNode = memo(ActionNodeInner)
