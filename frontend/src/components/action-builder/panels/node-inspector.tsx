'use client'

import { useId, useMemo } from 'react'
import type { Node } from '@xyflow/react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { CATEGORY_COLOR, NODE_CATALOG_BY_TYPE, PREVIEW_NOT_ENABLED_LABEL, categoryFor, isPreviewNodeType } from '../node-catalog'
import { isValidJsonConfig } from '../dag-analysis'
import type { ActionNodeData } from '../action-node'

interface NodeInspectorProps {
  node: Node<ActionNodeData>
  onPatch: (patch: Partial<ActionNodeData>) => void
  onDelete: () => void
}

/** Inspector body for the selected node: name, type badges, entry/terminal flags, JSON config. */
export function NodeInspector({ node, onPatch, onDelete }: NodeInspectorProps) {
  const fieldId = useId()
  const configValid = useMemo(() => isValidJsonConfig(node.data.config), [node.data.config])
  const isPreview = isPreviewNodeType(node.data.nodeType)

  return (
    <>
      {isPreview && (
        <div
          role="alert"
          className="rounded border border-amber-500/60 bg-amber-50 px-2 py-1.5 text-[11px] text-amber-800 dark:bg-amber-900/20 dark:text-amber-300"
        >
          <span className="font-semibold">{PREVIEW_NOT_ENABLED_LABEL}.</span> This node type is mirrored from the
          backend but cannot run yet; a run that reaches it will not settle. Its design track must land before this flow ships.
        </div>
      )}
      <div className="space-y-1.5">
        <Label htmlFor={`${fieldId}-name`} className="text-xs">Name</Label>
        <Input
          id={`${fieldId}-name`}
          value={node.data.label}
          onChange={(e) => onPatch({ label: e.target.value })}
          className="h-8 text-xs"
        />
      </div>
      <div className="space-y-1.5">
        <span className="text-xs font-medium">Type</span>
        <div className="flex items-center gap-1.5">
          <Badge className={`text-[10px] ${CATEGORY_COLOR[categoryFor(node.data.nodeType)].badge}`}>
            {node.data.nodeType}
          </Badge>
          {NODE_CATALOG_BY_TYPE[node.data.nodeType]?.requiresChain && (
            <Badge variant="outline" className="text-[9px]">on-chain</Badge>
          )}
          {isPreview && (
            <Badge variant="outline" className="border-amber-500 text-[9px] text-amber-700 dark:text-amber-400">preview</Badge>
          )}
        </div>
      </div>
      <div className="flex gap-4">
        <label className="flex items-center gap-1.5 text-xs">
          <Checkbox
            checked={node.data.isEntry}
            onCheckedChange={(v) => onPatch({ isEntry: !!v })}
          />
          Entry
        </label>
        <label className="flex items-center gap-1.5 text-xs">
          <Checkbox
            checked={node.data.isTerminal}
            onCheckedChange={(v) => onPatch({ isTerminal: !!v })}
          />
          Terminal
        </label>
      </div>
      <div className="space-y-1.5">
        <div className="flex items-center justify-between">
          <Label htmlFor={`${fieldId}-config`} className="text-xs">Config (JSON)</Label>
          {!configValid && <span className="text-[10px] text-red-600">invalid JSON</span>}
        </div>
        <Textarea
          id={`${fieldId}-config`}
          value={node.data.config}
          onChange={(e) => onPatch({ config: e.target.value })}
          className={`min-h-[160px] font-mono text-[11px] ${!configValid ? 'border-red-500' : ''}`}
        />
      </div>
      <Button size="sm" variant="destructive" className="w-full" onClick={onDelete}>
        Delete Node
      </Button>
    </>
  )
}
