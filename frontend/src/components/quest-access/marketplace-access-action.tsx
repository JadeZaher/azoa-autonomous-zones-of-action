'use client'

import { useState, useCallback, useEffect, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import { azoa, isOk } from '@/lib/azoa'
import type { QuestAccessRequest, QuestRunAccess } from 'azoa-sdk'
import { RunAccessBadge, AccessStatusBadge } from './access-badge'

/** Minimal quest shape this control needs off the marketplace catalog. */
export interface AccessQuest {
  id: string
  avatarId: string
  runAccess?: QuestRunAccess
  invitedAvatarIds?: string[]
}

interface Props {
  quest: AccessQuest
  /** The current authenticated avatar. */
  avatarId: string | null
  /** Rendered when the caller IS allowed to run (owner, Open, or invited/approved). */
  renderRunAction: () => ReactNode
}

/**
 * Per-card run-access gate for the marketplace list. Decides between the direct
 * run action (Open / owner / invited) and the request-to-take flow (InviteOnly
 * non-invited), reflecting any existing outbound request the caller holds.
 */
export function MarketplaceAccessAction({ quest, avatarId, renderRunAction }: Props) {
  const isOwner = avatarId != null && quest.avatarId === avatarId
  const isInvited = avatarId != null && (quest.invitedAvatarIds ?? []).includes(avatarId)
  const inviteOnly = quest.runAccess === 'InviteOnly'

  // Owner, Open quests, and invited avatars keep the direct run action unchanged.
  const gated = inviteOnly && !isOwner && !isInvited

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center gap-1.5">
        <RunAccessBadge runAccess={quest.runAccess} />
      </div>
      {gated ? (
        <RequestToTake questId={quest.id} />
      ) : (
        renderRunAction()
      )}
    </div>
  )
}

/**
 * Request-to-take control for an InviteOnly quest the caller can't yet run.
 * Loads the caller's existing request for this quest (if any) and renders the
 * appropriate state: request / pending+withdraw / approved-run / rejected.
 */
function RequestToTake({ questId }: { questId: string }) {
  const [existing, setExisting] = useState<QuestAccessRequest | null>(null)
  const [loading, setLoading] = useState(true)
  const [showNote, setShowNote] = useState(false)
  const [message, setMessage] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Scope the caller's outbound requests to this quest; keep the latest.
  const load = useCallback(async () => {
    setLoading(true)
    const res = await azoa.api.listMyAccessRequests()
    if (isOk(res)) {
      const mine = res.value
        .filter((r) => r.questId === questId)
        .sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1))
      setExisting(mine[0] ?? null)
    }
    setLoading(false)
  }, [questId])

  useEffect(() => {
    load()
  }, [load])

  const submitRequest = async () => {
    setBusy(true)
    setError(null)
    const res = await azoa.api.requestQuestAccess(questId, message.trim() || undefined)
    if (isOk(res)) {
      setExisting(res.value)
      setShowNote(false)
      setMessage('')
    } else {
      setError(res.error.message)
    }
    setBusy(false)
  }

  const withdraw = async () => {
    if (!existing) return
    setBusy(true)
    setError(null)
    const res = await azoa.api.withdrawAccessRequest(existing.id)
    if (isOk(res)) setExisting(res.value)
    else setError(res.error.message)
    setBusy(false)
  }

  if (loading) {
    return <div className="h-8 w-32 animate-pulse rounded bg-muted" />
  }

  // Pending → show status + withdraw.
  if (existing?.status === 'Pending') {
    return (
      <div className="flex flex-col gap-1.5">
        <div className="flex items-center gap-2">
          <AccessStatusBadge status="Pending" />
          <span className="text-xs text-muted-foreground">Requested — awaiting approval</span>
        </div>
        <Button size="sm" variant="outline" disabled={busy} onClick={withdraw}>
          {busy ? 'Withdrawing…' : 'Withdraw request'}
        </Button>
        {error && <p className="text-xs text-destructive">{error}</p>}
      </div>
    )
  }

  // Approved → the invite landed; the parent's run action should now be usable,
  // but if this control is still shown the list may be stale — prompt a refresh.
  if (existing?.status === 'Approved') {
    return (
      <div className="flex items-center gap-2">
        <AccessStatusBadge status="Approved" />
        <span className="text-xs text-muted-foreground">Access granted — reload to run</span>
      </div>
    )
  }

  // No active request, or previously Rejected/Withdrawn → allow a fresh request.
  return (
    <div className="flex flex-col gap-1.5">
      {existing && (existing.status === 'Rejected' || existing.status === 'Withdrawn') && (
        <div className="flex items-center gap-2">
          <AccessStatusBadge status={existing.status} />
          {existing.decisionReason && (
            <span className="text-xs text-muted-foreground">{existing.decisionReason}</span>
          )}
        </div>
      )}
      {showNote ? (
        <div className="flex flex-col gap-1.5">
          <Textarea
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            placeholder="Optional note to the owner…"
            className="min-h-14 text-xs"
          />
          <div className="flex gap-2">
            <Button size="sm" disabled={busy} onClick={submitRequest}>
              {busy ? 'Sending…' : 'Send request'}
            </Button>
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => setShowNote(false)}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        <Button size="sm" variant="secondary" disabled={busy} onClick={() => setShowNote(true)}>
          Request to take
        </Button>
      )}
      {error && <p className="text-xs text-destructive">{error}</p>}
    </div>
  )
}
