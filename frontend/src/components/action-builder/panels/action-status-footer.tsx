'use client'

import type { Edge, Node } from '@xyflow/react'
import { Button } from '@/components/ui/button'
import type { DagWarning } from '../dag-warnings'
import type { ActionNodeData } from '../action-node'
import { DagWarningList } from './dag-warning-list'

interface ActionStatusFooterProps {
  nodes: Node<ActionNodeData>[]
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
export function ActionStatusFooter({
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
}: ActionStatusFooterProps) {
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
