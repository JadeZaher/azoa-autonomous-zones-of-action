'use client'

import { memo } from 'react'
import { Handle, Position, type NodeProps } from '@xyflow/react'
import { Badge } from '@/components/ui/badge'

/** Data carried by every ecosystem-tree node on the canvas. */
export interface EcosystemNodeData {
  label: string
  refKind: string
  refId: string
  isRoot: boolean
  [key: string]: unknown
}

/** DappSeries nodes are amber (STAR family); nested STARODK nodes are indigo. */
const KIND_COLOR: Record<string, { border: string; dot: string }> = {
  DappSeries: { border: 'border-l-amber-500', dot: 'bg-amber-500' },
  StarOdk: { border: 'border-l-indigo-500', dot: 'bg-indigo-500' },
}

/**
 * Custom React Flow node for a single ecosystem tree entry. Mirrors the
 * quest-builder QuestNode card, keyed by ref kind instead of node category.
 */
function EcosystemNodeInner({ data, selected }: NodeProps) {
  const d = data as EcosystemNodeData
  const colors = KIND_COLOR[d.refKind] ?? KIND_COLOR.DappSeries

  return (
    <div
      className={`min-w-[180px] max-w-[240px] rounded-md border border-l-4 bg-card shadow-sm transition-shadow ${colors.border} ${selected ? 'ring-2 ring-primary' : ''}`}
    >
      <Handle type="target" position={Position.Top} className="!h-2.5 !w-2.5 !bg-muted-foreground" />

      <div className="p-2.5">
        <div className="flex items-start justify-between gap-2">
          <span className="text-sm font-medium leading-tight break-words">{d.label}</span>
          <span className={`mt-0.5 h-2 w-2 shrink-0 rounded-full ${colors.dot}`} />
        </div>
        <p className="mt-0.5 font-mono text-[10px] text-muted-foreground break-all">{d.refKind}</p>

        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          {d.isRoot && <Badge variant="outline" className="text-[9px] px-1 py-0">Root</Badge>}
          <Badge variant="outline" className="text-[9px] px-1 py-0 font-mono">{d.refId.slice(0, 8)}</Badge>
        </div>
      </div>

      <Handle type="source" position={Position.Bottom} className="!h-2.5 !w-2.5 !bg-muted-foreground" />
    </div>
  )
}

export const EcosystemNode = memo(EcosystemNodeInner)
