'use client'

import { useMemo } from 'react'
import type { Node } from '@xyflow/react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { Badge } from '@/components/ui/badge'
import { Checkbox } from '@/components/ui/checkbox'
import { CATEGORY_COLOR, NODE_CATALOG_BY_TYPE, categoryFor } from '../node-catalog'
import { isValidJsonConfig } from '../dag-analysis'
import type { QuestNodeData } from '../quest-node'

interface NodeInspectorProps {
  node: Node<QuestNodeData>
  onPatch: (patch: Partial<QuestNodeData>) => void
  onDelete: () => void
}

/** Inspector body for the selected node: name, type badges, entry/terminal flags, JSON config. */
export function NodeInspector({ node, onPatch, onDelete }: NodeInspectorProps) {
  const configValid = useMemo(() => isValidJsonConfig(node.data.config), [node.data.config])

  return (
    <>
      <div className="space-y-1.5">
        <Label className="text-xs">Name</Label>
        <Input
          value={node.data.label}
          onChange={(e) => onPatch({ label: e.target.value })}
          className="h-8 text-xs"
        />
      </div>
      <div className="space-y-1.5">
        <Label className="text-xs">Type</Label>
        <div className="flex items-center gap-1.5">
          <Badge className={`text-[10px] ${CATEGORY_COLOR[categoryFor(node.data.nodeType)].badge}`}>
            {node.data.nodeType}
          </Badge>
          {NODE_CATALOG_BY_TYPE[node.data.nodeType]?.requiresChain && (
            <Badge variant="outline" className="text-[9px]">on-chain</Badge>
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
          <Label className="text-xs">Config (JSON)</Label>
          {!configValid && <span className="text-[10px] text-red-600">invalid JSON</span>}
        </div>
        <Textarea
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
