'use client'

import { Button } from '@/components/ui/button'

interface PendingRequestNoticeProps {
  /** What the unresolved request was for, e.g. "A cash-out of 50.00 USD". */
  summary: string
  /** Why it is unresolved and what discarding means for this flow. */
  detail: string
  resumeLabel: string
  busy: boolean
  onResume: () => void
  onDiscard: () => void
}

/** An unresolved saved request blocks a new one until the person resumes or explicitly discards it. */
export function PendingRequestNotice({ summary, detail, resumeLabel, busy, onResume, onDiscard }: PendingRequestNoticeProps) {
  return (
    <div role="alert" className="space-y-3 rounded-lg border border-amber-500/40 bg-amber-500/5 p-3 text-sm">
      <p className="font-medium">{summary} has not been confirmed yet.</p>
      <p className="text-muted-foreground">{detail}</p>
      <div className="flex flex-wrap gap-2">
        <Button size="sm" onClick={onResume} disabled={busy}>{resumeLabel}</Button>
        <Button size="sm" variant="outline" onClick={onDiscard} disabled={busy}>Discard it and start a new request</Button>
      </div>
    </div>
  )
}
