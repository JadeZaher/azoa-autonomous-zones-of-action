'use client'

import { useState } from 'react'
import { cn } from '@/lib/utils'

interface JsonViewerProps {
  data: unknown
  depth?: number
}

export function JsonViewer({ data, depth = 0 }: JsonViewerProps) {
  if (data === null) return <span className="text-muted-foreground">null</span>
  if (data === undefined)
    return <span className="text-muted-foreground">undefined</span>

  if (typeof data === 'string')
    return <span className="text-green-600 dark:text-green-400">&quot;{data}&quot;</span>
  if (typeof data === 'number')
    return <span className="text-blue-600 dark:text-blue-400">{data}</span>
  if (typeof data === 'boolean')
    return (
      <span className="text-amber-600 dark:text-amber-400">
        {data ? 'true' : 'false'}
      </span>
    )

  if (Array.isArray(data)) {
    if (data.length === 0) return <span>{'[]'}</span>
    return <CollapsibleBlock label={`Array(${data.length})`} depth={depth} data={data} isArray />
  }

  if (typeof data === 'object') {
    const keys = Object.keys(data as Record<string, unknown>)
    if (keys.length === 0) return <span>{'{}'}</span>
    return <CollapsibleBlock label={`Object(${keys.length})`} depth={depth} data={data as Record<string, unknown>} isArray={false} />
  }

  return <span>{String(data)}</span>
}

function CollapsibleBlock({
  label,
  depth,
  data,
  isArray,
}: {
  label: string
  depth: number
  data: unknown[] | Record<string, unknown>
  isArray: boolean
}) {
  const [collapsed, setCollapsed] = useState(depth > 2)

  const entries = isArray
    ? (data as unknown[]).map((v, i) => [String(i), v] as const)
    : Object.entries(data as Record<string, unknown>)

  return (
    <div className={cn(depth > 0 && 'ml-4')}>
      <button
        type="button"
        onClick={() => setCollapsed(!collapsed)}
        className="text-xs text-muted-foreground hover:text-foreground"
      >
        {collapsed ? '+ ' : '- '}
        {label}
      </button>
      {!collapsed && (
        <div className="ml-2 border-l border-border pl-2">
          {entries.map(([key, value]) => (
            <div key={key} className="flex gap-1">
              <span className="shrink-0 text-sm text-purple-600 dark:text-purple-400">
                {key}:
              </span>
              <JsonViewer data={value} depth={depth + 1} />
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
