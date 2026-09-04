'use client'

import type { Edge, Node } from '@xyflow/react'
import { Button } from '@/components/ui/button'
import type { DagWarning } from '../dag-warnings'
import type { QuestNodeData } from '../quest-node'
import { DagWarningList } from './dag-warning-list'

interface QuestStatusFooterProps {
  nodes: Node<QuestNodeData>[]
  edges: Edge[]
  warnings: DagWarning[]
  onFocusNode: (nodeId: string) => void
  /** The currently-selected node, if any — highlights its findings in the list below. */
  highlightedNodeId?: string | null
  submitting?: boolean
  submitLabel: string
  hasBlockingErrors: boolean
  submitBlockedReason?: string
  onSubmit: () => void
}

/** Inspector footer: graph counts, the DAG warning list, and the submit button. */
export function QuestStatusFooter({
  nodes,
  edges,
  warnings,
  onFocusNode,
  highlightedNodeId,
  submitting,
  submitLabel,
  hasBlockingErrors,
  submitBlockedReason,
  onSubmit,
}: QuestStatusFooterProps) {
  return (
    <div className="space-y-1 border-t p-2 text-[11px] text-muted-foreground">
      <div className="flex justify-between">
        <span>{nodes.length} nodes · {edges.length} edges</span>
      </div>
      <DagWarningList warnings={warnings} nodes={nodes} onFocusNode={onFocusNode} highlightedNodeId={highlightedNodeId} />
      <Button
        className="w-full"
        size="sm"
        disabled={submitting || nodes.length === 0 || hasBlockingErrors}
        onClick={onSubmit}
        title={submitBlockedReason}
      >
        {submitting ? 'Saving…' : submitLabel}
      </Button>
    </div>
  )
}
