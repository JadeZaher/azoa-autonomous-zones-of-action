'use client'

/**
 * The federated sign-in buttons, shared by the login and register pages so both
 * surfaces stay identical. Lives in lib/ alongside the client it drives.
 */

import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Separator } from '@/components/ui/separator'
import { Loader2 } from 'lucide-react'
import {
  beginProviderSignIn,
  fetchEnabledProviders,
  type OAuthProvider,
} from './oauth'

export function GitHubMark({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z" />
    </svg>
  )
}

export function GoogleMark({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 24 24" aria-hidden="true">
      <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 01-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z" fill="#4285F4" />
      <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853" />
      <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05" />
      <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335" />
    </svg>
  )
}

const PROVIDER_LABEL: Record<OAuthProvider, string> = {
  github: 'GitHub',
  google: 'Google',
}

export function ProviderMark({
  provider,
  className,
}: {
  provider: OAuthProvider
  className?: string
}) {
  return provider === 'github'
    ? <GitHubMark className={className} />
    : <GoogleMark className={className} />
}

/**
 * Renders one button per provider the NODE actually has credentials for.
 *
 * A node with no configured provider renders nothing at all — better than a
 * decorative button that dead-ends, which is what shipped before this change.
 */
export function OAuthProviderButtons({
  returnTo,
  tenant,
  label = 'or continue with',
}: {
  returnTo?: string | null
  tenant?: string | null
  label?: string
}) {
  const [providers, setProviders] = useState<OAuthProvider[] | null>(null)
  const [pending, setPending] = useState<OAuthProvider | null>(null)

  useEffect(() => {
    let active = true
    fetchEnabledProviders().then((enabled) => {
      if (active) setProviders(enabled)
    })
    return () => {
      active = false
    }
  }, [])

  // Still asking, or the node offers none: render nothing.
  if (providers === null || providers.length === 0) return null

  return (
    <div className="space-y-6" data-testid="oauth-provider-buttons">
      <div className="relative">
        <div className="absolute inset-0 flex items-center">
          <Separator />
        </div>
        <div className="relative flex justify-center text-xs uppercase">
          <span className="bg-background px-2 text-muted-foreground">{label}</span>
        </div>
      </div>

      <div className={providers.length > 1 ? 'grid grid-cols-2 gap-3' : 'grid grid-cols-1'}>
        {providers.map((provider) => (
          <Button
            key={provider}
            variant="outline"
            type="button"
            className="h-11"
            disabled={pending !== null}
            onClick={() => {
              setPending(provider)
              beginProviderSignIn(provider, { returnTo, tenant })
            }}
          >
            {pending === provider ? (
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
            ) : (
              <ProviderMark provider={provider} className="mr-2 h-4 w-4" />
            )}
            {PROVIDER_LABEL[provider]}
          </Button>
        ))}
      </div>
    </div>
  )
}
