'use client'

/**
 * Manage federated providers attached to the signed-in avatar (Phase 1B, D18).
 *
 * This page is the ONLY route by which a provider becomes attached to an existing
 * account. Rule 1 of D17: an OAuth identity is never auto-linked by matching email;
 * linking requires an authenticated session and an explicit user action, which is
 * exactly what pressing "Connect" here is.
 */

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useAzoaAuth } from '@/lib/azoa-auth'
import { ProviderMark } from '@/lib/oauth-buttons'
import {
  OAUTH_PROVIDERS,
  beginProviderLink,
  fetchEnabledProviders,
  listLinkedProviders,
  unlinkProvider,
  type LinkedProvider,
  type OAuthProvider,
} from '@/lib/oauth'

const PROVIDER_LABEL: Record<OAuthProvider, string> = {
  github: 'GitHub',
  google: 'Google',
}

export default function LinkedAccountsPage() {
  const { isAuthenticated, loading } = useAzoaAuth()
  const [available, setAvailable] = useState<OAuthProvider[]>([])
  const [linked, setLinked] = useState<LinkedProvider[]>([])
  const [busy, setBusy] = useState<OAuthProvider | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [ready, setReady] = useState(false)

  const refresh = useCallback(async () => {
    const [enabled, current] = await Promise.all([
      fetchEnabledProviders(),
      listLinkedProviders(),
    ])
    setAvailable(enabled)
    setLinked(current)
    setReady(true)
  }, [])

  useEffect(() => {
    if (loading) return
    // Async continuation rather than a synchronous setState in the effect body:
    // this synchronises with an external system (the links endpoint), and setting
    // state inline would trigger the cascading re-render the lint rule guards.
    void (async () => {
      if (isAuthenticated) await refresh()
      else setReady(true)
    })()
  }, [loading, isAuthenticated, refresh])

  async function handleLink(provider: OAuthProvider) {
    setError(null)
    setBusy(provider)
    try {
      await beginProviderLink(provider, { returnTo: '/linked-accounts' })
    } catch (linkError) {
      setError(linkError instanceof Error ? linkError.message : 'Could not start linking.')
      setBusy(null)
    }
  }

  async function handleUnlink(provider: OAuthProvider) {
    setError(null)
    setBusy(provider)
    try {
      await unlinkProvider(provider)
      await refresh()
    } catch (unlinkError) {
      setError(unlinkError instanceof Error ? unlinkError.message : 'Could not unlink.')
    } finally {
      setBusy(null)
    }
  }

  if (loading || !ready) {
    return (
      <div className="flex justify-center py-16" role="status">
        <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
      </div>
    )
  }

  if (!isAuthenticated) {
    return (
      <div className="space-y-4">
        <h1 className="text-2xl font-bold tracking-tight">Linked accounts</h1>
        <p className="text-sm text-muted-foreground">
          Sign in to manage the providers connected to your avatar.
        </p>
        <Link
          href="/login?returnTo=/linked-accounts"
          className="font-medium text-primary underline-offset-4 hover:underline"
        >
          Go to sign in
        </Link>
      </div>
    )
  }

  const linkedSet = new Set(linked.map((entry) => entry.provider))
  const offered = OAUTH_PROVIDERS.filter((provider) => available.includes(provider))

  return (
    <div className="space-y-6">
      <div className="space-y-2">
        <h1 className="text-2xl font-bold tracking-tight">Linked accounts</h1>
        <p className="text-sm text-muted-foreground">
          Connect a provider to sign in faster. Connecting never merges another
          account into yours — it attaches the provider to the avatar you are
          signed in as right now.
        </p>
      </div>

      {error && (
        <div
          role="alert"
          className="flex items-start gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-3 text-sm text-destructive"
        >
          <span className="mt-0.5 shrink-0 select-none text-base">!</span>
          <p>{error}</p>
        </div>
      )}

      {offered.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          This node has no federated sign-in providers configured.
        </p>
      ) : (
        <ul className="space-y-3">
          {offered.map((provider) => {
            const entry = linked.find((candidate) => candidate.provider === provider)
            return (
              <li
                key={provider}
                className="flex items-center justify-between gap-4 rounded-lg border border-border px-4 py-3"
              >
                <div className="flex items-center gap-3">
                  <ProviderMark provider={provider} className="h-5 w-5" />
                  <div>
                    <p className="text-sm font-medium">{PROVIDER_LABEL[provider]}</p>
                    <p className="text-xs text-muted-foreground">
                      {entry
                        ? entry.emailAtLink ?? entry.displayNameAtLink ?? 'Connected'
                        : 'Not connected'}
                    </p>
                  </div>
                </div>

                <Button
                  variant={linkedSet.has(provider) ? 'outline' : 'default'}
                  type="button"
                  disabled={busy !== null}
                  onClick={() =>
                    linkedSet.has(provider) ? handleUnlink(provider) : handleLink(provider)
                  }
                >
                  {busy === provider && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                  {linkedSet.has(provider) ? 'Disconnect' : 'Connect'}
                </Button>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
