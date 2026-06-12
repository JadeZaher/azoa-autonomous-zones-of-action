'use client'

import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { useDebug } from '@/lib/debug-context'

/**
 * Global debug-mode switcher. When on, the SDK logs every request/response and
 * the backend's server-side exception chain is surfaced in error dumps (see
 * the Functional Test Runner). The choice persists across reloads and takes
 * effect immediately — no page reload.
 */
export function DebugSwitcher({ size = 'sm' }: { size?: 'sm' | 'default' }) {
  const { debug, setDebug } = useDebug()

  return (
    <Select
      value={debug ? 'on' : 'off'}
      onValueChange={(v) => { if (v) setDebug(v === 'on') }}
    >
      <SelectTrigger
        size={size}
        aria-label="Toggle debug mode"
        className="gap-2"
      >
        <span
          className={`inline-block size-2 rounded-full ${debug ? 'bg-emerald-500' : 'bg-muted-foreground'}`}
          aria-hidden
        />
        <SelectValue />
      </SelectTrigger>
      <SelectContent align="end">
        <SelectItem value="on">Debug: On</SelectItem>
        <SelectItem value="off">Debug: Off</SelectItem>
      </SelectContent>
    </Select>
  )
}
