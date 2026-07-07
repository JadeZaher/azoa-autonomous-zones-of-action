'use client'

import { useState, useCallback, useEffect } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Separator } from '@/components/ui/separator'
import { Textarea } from '@/components/ui/textarea'
import { ErrorBanner } from '@/components/shared/error-banner'
import { X, UserPlus } from 'lucide-react'
import { azoa, isOk } from '@/lib/azoa'
import type { QuestAccessRequest, QuestRunAccess } from 'azoa-sdk'

interface Props {
  questId: string
  /** Current run-access + invite set off the loaded quest. */
  runAccess: QuestRunAccess | undefined
  invitedAvatarIds: string[]
  /** Re-fetch the parent quest so run-access/invite fields refresh. */
  onChanged: () => void
}

/**
 * Owner-only run-access surface: toggle Open ↔ InviteOnly, manage the invite
 * list directly, and work the pending-request approval queue.
 */
export function OwnerAccessControls({ questId, runAccess, invitedAvatarIds, onChanged }: Props) {
  const inviteOnly = runAccess === 'InviteOnly'
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [newInvite, setNewInvite] = useState('')

  const toggleMode = async (next: boolean) => {
    setBusy(true)
    setError(null)
    const res = await azoa.api.setQuestRunAccess(questId, {
      runAccess: next ? 'InviteOnly' : 'Open',
    })
    if (isOk(res)) onChanged()
    else setError(res.error.message)
    setBusy(false)
  }

  const addInvite = async () => {
    const id = newInvite.trim()
    if (!id) return
    setBusy(true)
    setError(null)
    const res = await azoa.api.inviteAvatar(questId, id)
    if (isOk(res)) {
      setNewInvite('')
      onChanged()
    } else {
      setError(res.error.message)
    }
    setBusy(false)
  }

  const revoke = async (avatarId: string) => {
    setBusy(true)
    setError(null)
    const res = await azoa.api.revokeInvite(questId, avatarId)
    if (isOk(res)) onChanged()
    else setError(res.error.message)
    setBusy(false)
  }

  return (
    <div className="flex flex-col gap-4 rounded-md border p-3">
      <div>
        <h4 className="text-sm font-semibold">Run access</h4>
        <p className="text-xs text-muted-foreground">
          Control who may run or fork this quest — independent of marketplace visibility.
        </p>
      </div>

      {/* Open ↔ InviteOnly toggle */}
      <label className="flex items-center gap-3 text-sm">
        <Switch checked={inviteOnly} disabled={busy} onCheckedChange={(v) => toggleMode(v)} />
        <span>
          <strong>Invite-only</strong>
          <span className="block text-xs text-muted-foreground">
            {inviteOnly
              ? 'Only you and invited avatars can run or fork this quest.'
              : 'Anyone who can view this quest may run or fork it.'}
          </span>
        </span>
      </label>

      {inviteOnly && (
        <>
          <Separator />
          {/* Invite manager */}
          <div className="flex flex-col gap-2">
            <Label className="text-xs">Invited avatars ({invitedAvatarIds.length})</Label>
            <div className="flex gap-2">
              <Input
                value={newInvite}
                onChange={(e) => setNewInvite(e.target.value)}
                placeholder="Avatar ID to invite…"
                className="text-xs"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault()
                    void addInvite()
                  }
                }}
              />
              <Button size="sm" disabled={busy || !newInvite.trim()} onClick={addInvite}>
                <UserPlus className="mr-1 h-3.5 w-3.5" aria-hidden />
                Invite
              </Button>
            </div>
            {invitedAvatarIds.length > 0 ? (
              <ul className="flex flex-col gap-1">
                {invitedAvatarIds.map((id) => (
                  <li
                    key={id}
                    className="flex items-center justify-between rounded border bg-muted/40 px-2 py-1 text-xs"
                  >
                    <code className="font-mono">{id}</code>
                    <Button
                      size="sm"
                      variant="ghost"
                      disabled={busy}
                      onClick={() => revoke(id)}
                      className="h-6 px-1.5 text-destructive hover:text-destructive"
                    >
                      <X className="h-3.5 w-3.5" aria-hidden />
                      <span className="sr-only">Revoke invite</span>
                    </Button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-xs text-muted-foreground">No avatars invited yet.</p>
            )}
          </div>

          <Separator />
          <ApprovalQueue questId={questId} onDecided={onChanged} />
        </>
      )}

      {error && <ErrorBanner message={error} />}
    </div>
  )
}

/** Owner's pending-request approval queue with approve/reject + optional reason. */
function ApprovalQueue({ questId, onDecided }: { questId: string; onDecided: () => void }) {
  const [requests, setRequests] = useState<QuestAccessRequest[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [reasons, setReasons] = useState<Record<string, string>>({})
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    const res = await azoa.api.listAccessRequests(questId, 'Pending')
    if (isOk(res)) setRequests(res.value)
    else setError(res.error.message)
    setLoading(false)
  }, [questId])

  useEffect(() => {
    load()
  }, [load])

  const decide = async (req: QuestAccessRequest, approve: boolean) => {
    setBusyId(req.id)
    setError(null)
    const res = await azoa.api.decideAccessRequest(req.id, approve, reasons[req.id]?.trim() || undefined)
    if (isOk(res)) {
      // Approval appends the requester to the invite set — refresh both queue + quest.
      await load()
      onDecided()
    } else {
      setError(res.error.message)
    }
    setBusyId(null)
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <Label className="text-xs">Pending requests ({requests.length})</Label>
        <Button size="sm" variant="ghost" onClick={load} disabled={loading} className="h-6 text-xs">
          {loading ? 'Loading…' : 'Refresh'}
        </Button>
      </div>

      {error && <ErrorBanner message={error} onRetry={load} />}

      {!loading && requests.length === 0 ? (
        <p className="text-xs text-muted-foreground">No pending requests.</p>
      ) : (
        <ul className="flex flex-col gap-2">
          {requests.map((r) => (
            <li key={r.id} className="flex flex-col gap-2 rounded border p-2">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <code className="block truncate font-mono text-xs">{r.requesterAvatarId}</code>
                  {r.message && (
                    <p className="mt-0.5 text-xs text-muted-foreground">“{r.message}”</p>
                  )}
                  <p className="mt-0.5 text-[10px] text-muted-foreground">
                    {new Date(r.createdAt).toLocaleString()}
                  </p>
                </div>
              </div>
              <Textarea
                value={reasons[r.id] ?? ''}
                onChange={(e) => setReasons((s) => ({ ...s, [r.id]: e.target.value }))}
                placeholder="Optional reason (approve or reject)…"
                className="min-h-10 text-xs"
              />
              <div className="flex gap-2">
                <Button
                  size="sm"
                  disabled={busyId === r.id}
                  onClick={() => decide(r, true)}
                >
                  {busyId === r.id ? 'Working…' : 'Approve'}
                </Button>
                <Button
                  size="sm"
                  variant="destructive"
                  disabled={busyId === r.id}
                  onClick={() => decide(r, false)}
                >
                  Reject
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
