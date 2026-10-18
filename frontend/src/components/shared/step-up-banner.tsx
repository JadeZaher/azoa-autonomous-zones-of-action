'use client'

import Link from 'next/link'
import { buttonVariants } from '@/components/ui/button'

interface StepUpBannerProps {
  /** Path to return to once re-authentication refreshes the operator's `AuthTime` claim. */
  returnTo: string
}

/**
 * `RecentNodeOperatorSession` refusal prompt (X-7): same shape as the dashboard's first-party
 * step-up banner (`(dashboard)/kyc/page.tsx`) so operators see one consistent pattern. A plain
 * `/login` round trip is enough — the node operator identity's own login mints the fresh
 * `AuthTime` claim these Operator-policy routes check, no separate operator console needed.
 */
export function StepUpBanner({ returnTo }: StepUpBannerProps) {
  return (
    <div
      role="status"
      className="flex flex-col gap-3 border border-primary bg-primary/5 p-4 text-sm sm:flex-row sm:items-center sm:justify-between"
    >
      <p>Fresh authentication protects this operator action from an unattended session.</p>
      <Link
        href={`/login?reason=step-up&returnTo=${encodeURIComponent(returnTo)}`}
        className={buttonVariants({ className: 'min-h-11 shrink-0 rounded-none' })}
      >
        Sign in again
      </Link>
    </div>
  )
}
