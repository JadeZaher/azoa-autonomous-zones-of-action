'use client'

import type { Edge } from '@xyflow/react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { EDGE_TYPE_OPTIONS, type EdgeData } from '../edge-model'

interface EdgeInspectorProps {
  edge: Edge
  onPatch: (patch: Partial<EdgeData>) => void
  onDelete: () => void
}

/** Inspector body for the selected edge: edge type picker and the Conditional condition (G2). */
export function EdgeInspector({ edge, onPatch, onDelete }: EdgeInspectorProps) {
  const data = (edge.data as EdgeData) ?? {}
  const edgeType = data.edgeType ?? 'Control'
  const condition = data.condition ?? ''
  const missingCondition = edgeType === 'Conditional' && !condition.trim()

  return (
    <div className="space-y-3">
      <div className="space-y-1.5">
        <Label className="text-xs">Edge Type</Label>
        <div className="flex gap-2">
          {EDGE_TYPE_OPTIONS.map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => onPatch({ edgeType: t })}
              className={`rounded border px-2 py-1 text-xs transition-colors ${
                edgeType === t
                  ? 'border-primary bg-primary text-primary-foreground'
                  : 'border-border bg-background hover:bg-accent'
              }`}
            >
              {t}
            </button>
          ))}
        </div>
      </div>

      {edgeType === 'Conditional' && (
        <div className="space-y-1.5">
          <div className="flex items-center justify-between">
            <Label className="text-xs">Condition</Label>
            {missingCondition && (
              <span className="text-[10px] text-red-600">required</span>
            )}
          </div>
          <Input
            value={condition}
            onChange={(e) => onPatch({ condition: e.target.value })}
            placeholder="e.g. true, false, output.ok == true"
            className={`h-8 text-xs ${missingCondition ? 'border-red-500' : ''}`}
          />
          {missingCondition && (
            <p className="text-[10px] text-red-600">
              Conditional edges require a non-empty condition. The server will reject this edge.
            </p>
          )}
        </div>
      )}

      <Button size="sm" variant="destructive" className="w-full" onClick={onDelete}>
        Delete Edge
      </Button>
    </div>
  )
}
