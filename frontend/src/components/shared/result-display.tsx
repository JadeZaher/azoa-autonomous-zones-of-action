'use client'

import { cn } from '@/lib/utils'
import { JsonViewer } from './json-viewer'

interface ResultDisplayProps {
  result?: unknown
  isError?: boolean
  message?: string
  loading?: boolean
}

export function ResultDisplay({
  result,
  isError = false,
  message,
  loading = false,
}: ResultDisplayProps) {
  if (loading) {
    return (
      <div className="space-y-2 rounded-lg border p-4 animate-pulse">
        <div className="h-4 w-24 rounded bg-muted" />
        <div className="h-3 w-full rounded bg-muted" />
      </div>
    )
  }

  if (!result && !message) return null

  return (
    <div
      className={cn(
        'rounded-lg border p-4 text-sm',
        isError
          ? 'border-destructive/40 bg-destructive/5'
          : 'border-emerald-500/30 bg-emerald-500/5'
      )}
    >
      <p className={cn('mb-2 text-xs font-semibold uppercase tracking-wider', isError ? 'text-destructive' : 'text-emerald-400')}>
        {isError ? 'Error' : 'Success'}
      </p>
      {message && <p className="mb-2">{message}</p>}
      {result !== undefined && result !== null && (
        <div className="rounded-md bg-muted/50 p-3 text-xs">
          <JsonViewer data={result} />
        </div>
      )}
    </div>
  )
}
