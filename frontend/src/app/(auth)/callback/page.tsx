'use client'

/**
 * Federated sign-in landing page (Phase 1B, D18).
 *
 * The backend callback redirects here with a single-use opaque code. This page
 * spends it EXACTLY once for the real AZOA JWT, then continues into the app.
 */

import { useEffect, useRef, useState } from 'react'
import { useRouter } from 'next/navigation'
import Link from 'next/link'
import { Loader2 } from 'lucide-react'
import { exchangeOAuthCode, safeReturnTo } from '@/lib/oauth'

/** Backend error codes are opaque on purpose; map them to something a user can act on. */
const ERROR_COPY: Record<string, string> = {
  sign_in_failed: 'That sign-in could not be completed. Please try again.',
  link_failed: 'That account could not be linked. Start the linking flow again.',
  link_conflict: 'That provider account is already linked to a different avatar.',
  unsupported_provider: 'That sign-in provider is not available on this node.',
}

export default function OAuthCallbackPage() {
  const router = useRouter()
  const [error, setError] = useState<string | null>(null)

  // The code is spent server-side on first use. React 18 StrictMode mounts effects
  // twice in development, and without this guard the second run would redeem an
  // already-consumed code and show a spurious failure.
  const startedRef = useRef(false)

  useEffect(() => {
    if (startedRef.current) return
    startedRef.current = true

    // The whole body runs in an async continuation rather than synchronously in
    // the effect: this is a one-shot side effect against an external system (the
    // exchange endpoint), and setting state synchronously here would trigger the
    // cascading re-render react-hooks/set-state-in-effect warns about.
    void (async () => {
      const params = new URLSearchParams(window.location.search)
      const code = params.get('code')
      const failure = params.get('error')
      const linked = params.get('linked')
      const returnTo = safeReturnTo(params.get('returnTo'))

      if (failure) {
        setError(ERROR_COPY[failure] ?? 'That sign-in could not be completed.')
        return
      }

      // A successful LINK carries no code — the session that started it is still live.
      if (linked) {
        router.replace(returnTo ?? '/linked-accounts?linked=' + encodeURIComponent(linked))
        return
      }

      if (!code) {
        setError('That sign-in link is incomplete. Please start again.')
        return
      }

      try {
        const result = await exchangeOAuthCode(code)
        // Strip the code from the address bar before navigating, so it never
        // lingers in browser history.
        window.history.replaceState({}, '', window.location.pathname)
        router.replace(result.returnTo ?? returnTo ?? '/overview')
      } catch (exchangeError: unknown) {
        setError(
          exchangeError instanceof Error
            ? exchangeError.message
            : 'That sign-in link is invalid or has expired.'
        )
      }
    })()
  }, [router])

  if (error) {
    return (
      <div className="space-y-6">
        <div className="space-y-2 text-center lg:text-left">
          <h1 className="text-2xl font-bold tracking-tight">Sign-in failed</h1>
        </div>
        <div
          role="alert"
          className="flex items-start gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-destructive"
        >
          <span className="mt-0.5 shrink-0 select-none text-base">!</span>
          <p>{error}</p>
        </div>
        <p className="text-center text-sm text-muted-foreground">
          <Link
            href="/login"
            className="font-medium text-primary underline-offset-4 hover:underline"
          >
            Back to sign in
          </Link>
        </p>
      </div>
    )
  }

  return (
    <div className="flex flex-col items-center justify-center gap-4 py-16" role="status">
      <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
      <p className="text-sm text-muted-foreground">Completing sign-in…</p>
    </div>
  )
}
