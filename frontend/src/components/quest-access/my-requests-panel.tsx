'use client'

import { useState, useCallback, useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { ErrorBanner } from '@/components/shared/error-banner'
import { LoadingSkeleton } from '@/components/shared/loading-skeleton'
import { azoa, isOk } from '@/lib/azoa'
import type { QuestAccessRequest } from 'azoa-sdk'
import { AccessStatusBadge } from './access-badge'

/**
 * Requester-facing outbound view: every access request this avatar has opened,
 * with a Withdraw affordance on the Pending ones (listMyAccessRequests +
 * withdrawAccessRequest).
 */
export function MyRequestsPanel() {
  const [requests, setRequests] = useState<QuestAccessRequest[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    const res = await azoa.api.listMyAccessRequests()
    if (isOk(res)) {
      setRequests(
        [...res.value].sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1)),
      )
    } else {
      setError(res.error.message)
    }
    setLoading(false)
  }, [])

  useEffect(() => {
    load()
  }, [load])

  const withdraw = async (req: QuestAccessRequest) => {
    setBusyId(req.id)
    setError(null)
    const res = await azoa.api.withdrawAccessRequest(req.id)
    if (isOk(res)) {
      setRequests((rs) => rs.map((r) => (r.id === req.id ? res.value : r)))
    } else {
      setError(res.error.message)
    }
    setBusyId(null)
  }

  return (
    <div className="flex flex-col gap-3 rounded-lg border bg-card p-4">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-sm font-semibold">My access requests</h3>
          <p className="text-sm text-muted-foreground">
            Requests you've opened to run invite-only quests.
          </p>
        </div>
        <Button size="sm" variant="outline" onClick={load} disabled={loading}>
          {loading ? 'Loading…' : 'Reload'}
        </Button>
      </div>

      {error && <ErrorBanner message={error} onRetry={load} />}
      {loading && <LoadingSkeleton />}

      {!loading && requests.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          You haven't requested access to any invite-only quests yet.
        </p>
      ) : (
        <ul className="divide-y rounded-md border">
          {requests.map((r) => (
            <li key={r.id} className="flex items-center gap-3 px-3 py-2.5">
              <div className="min-w-0 flex-1">
                <code className="block truncate font-mono text-xs">quest {r.questId}</code>
                {r.decisionReason && (
                  <p className="mt-0.5 text-xs text-muted-foreground">{r.decisionReason}</p>
                )}
                <p className="mt-0.5 text-[10px] text-muted-foreground">
                  requested {new Date(r.createdAt).toLocaleString()}
                  {r.decidedAt && ` · decided ${new Date(r.decidedAt).toLocaleString()}`}
                </p>
              </div>
              <AccessStatusBadge status={r.status} />
              {r.status === 'Pending' && (
                <Button
                  size="sm"
                  variant="outline"
                  disabled={busyId === r.id}
                  onClick={() => withdraw(r)}
                >
                  {busyId === r.id ? 'Withdrawing…' : 'Withdraw'}
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
