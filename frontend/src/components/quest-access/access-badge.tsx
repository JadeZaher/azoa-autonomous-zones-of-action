'use client'

import { Badge } from '@/components/ui/badge'
import { Lock, Globe } from 'lucide-react'
import type { QuestRunAccess, QuestAccessRequestStatus } from 'azoa-sdk'

/**
 * Run-access badge — the run-authorization dimension orthogonal to `isPublic`.
 * `Open` = anyone who can view may run/fork (today's default); `InviteOnly` =
 * only owner + invited avatars may run/fork (quest stays viewable).
 */
export function RunAccessBadge({ runAccess }: { runAccess: QuestRunAccess | undefined }) {
  if (runAccess === 'InviteOnly') {
    return (
      <Badge className="gap-1 bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300">
        <Lock className="h-3 w-3" aria-hidden />
        Invite-only
      </Badge>
    )
  }
  return (
    <Badge className="gap-1 bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-300">
      <Globe className="h-3 w-3" aria-hidden />
      Open
    </Badge>
  )
}

/** Colored badge for an outbound/queued access-request status. */
export function AccessStatusBadge({ status }: { status: QuestAccessRequestStatus }) {
  const colors: Record<QuestAccessRequestStatus, string> = {
    Pending: 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300',
    Approved: 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-300',
    Rejected: 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-300',
    Withdrawn: 'bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-300',
  }
  return <Badge className={colors[status]}>{status}</Badge>
}
