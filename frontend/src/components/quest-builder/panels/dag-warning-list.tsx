'use client'

import type { Node } from '@xyflow/react'
import type { DagWarning } from '../dag-warnings'
import type { QuestNodeData } from '../quest-node'

interface DagWarningListProps {
  warnings: DagWarning[]
  nodes: Node<QuestNodeData>[]
  onFocusNode: (nodeId: string) => void
  /** The currently-selected node, if any — its findings get an emphasis ring (the reverse of clicking a warning). */
  highlightedNodeId?: string | null
}

/** Pre-check warning list; each entry that names a live node jumps to it on click. */
export function DagWarningList({ warnings, nodes, onFocusNode, highlightedNodeId }: DagWarningListProps) {
  if (warnings.length === 0) return null

  return (
    <ul className="space-y-0.5 rounded border border-amber-500/40 bg-amber-50 p-1.5 text-[10px] dark:bg-amber-900/20">
      {warnings.map((w, i) => {
        const target = w.nodeIds?.find((id) => nodes.some((n) => n.id === id))
        const color = w.error ? 'text-red-700 dark:text-red-400' : 'text-amber-800 dark:text-amber-300'
        const isHighlighted = !!highlightedNodeId && !!w.nodeIds?.includes(highlightedNodeId)
        return (
          <li
            key={i}
            className={`flex gap-1 rounded px-0.5 ${color} ${
              isHighlighted ? 'bg-primary/10 ring-1 ring-primary/40' : ''
            }`}
          >
            <span aria-hidden>{w.error ? '✕' : '⚠'}</span>
            {target ? (
              <button
                type="button"
                onClick={() => onFocusNode(target)}
                className="text-left underline decoration-dotted underline-offset-2 hover:decoration-solid"
                title="Jump to the offending node"
              >
                {w.text}
              </button>
            ) : (
              <span>{w.text}</span>
            )}
          </li>
        )
      })}
    </ul>
  )
}
