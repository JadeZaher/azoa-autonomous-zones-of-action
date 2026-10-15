'use client'

/**
 * Federated sign-in client (Phase 1B, D18).
 *
 * The browser never sees a client secret and never performs a token exchange. It
 * only ever does three things:
 *   1. navigate to the backend's `start` endpoint (a top-level navigation, so the
 *      provider's redirect and the handler's `state` cookie both work),
 *   2. hand the opaque code from the callback back to `exchange`,
 *   3. adopt the AZOA JWT that comes back.
 *
 * `state` and PKCE are generated and validated entirely server-side by the ASP.NET
 * OAuth handler — there is deliberately no PKCE code in this file to get wrong.
 */

import { azoa } from './azoa'
import { readInitialApiUrl } from './runtime-config'

export type OAuthProvider = 'google' | 'github'

/** Every provider this client knows how to render, in display order. */
export const OAUTH_PROVIDERS: readonly OAuthProvider[] = ['github', 'google']

/**
 * Storage keys owned by the SDK's SessionManager (its documented defaults).
 * Writing them and then calling `azoa.session.restore()` is how a token obtained
 * outside `session.login()` is adopted, using only the SDK's public surface.
 */
const SESSION_TOKEN_KEY = 'azoa_token'
const SESSION_USER_KEY = 'azoa_user_id'

export interface OAuthExchangeResult {
  token: string
  userId: string
  username: string
  email: string
  provider: OAuthProvider
  returnTo?: string | null
}

export interface LinkedProvider {
  provider: OAuthProvider
  emailAtLink?: string | null
  displayNameAtLink?: string | null
  linkedAt: string
  lastLoginAt?: string | null
}

function apiBase(): string {
  return readInitialApiUrl().replace(/\/$/, '')
}

/**
 * Only a same-site relative path survives. Mirrors the backend's own check —
 * an absolute or protocol-relative target would be an open redirect.
 */
export function safeReturnTo(candidate: string | null | undefined): string | null {
  if (!candidate) return null
  const value = candidate.trim()
  if (!value.startsWith('/')) return null
  if (value.startsWith('//')) return null
  if (value.includes('\\')) return null
  return value
}

/** Providers this node can actually complete a sign-in with. */
export async function fetchEnabledProviders(): Promise<OAuthProvider[]> {
  try {
    const response = await fetch(`${apiBase()}/auth/oauth/providers`, {
      headers: { Accept: 'application/json' },
    })
    if (!response.ok) return []
    const body = await response.json()
    const names: unknown = body?.result ?? body?.Result
    if (!Array.isArray(names)) return []
    return names.filter((name): name is OAuthProvider =>
      name === 'google' || name === 'github'
    )
  } catch {
    // A node that cannot answer simply offers no federated buttons.
    return []
  }
}

/**
 * Builds the backend URL that begins a sign-in. Exposed separately from the
 * navigation so it can be asserted in a test without a jsdom navigation stub.
 */
export function buildSignInStartUrl(
  provider: OAuthProvider,
  options?: { returnTo?: string | null; tenant?: string | null; ticket?: string | null }
): string {
  const params = new URLSearchParams()
  const returnTo = safeReturnTo(options?.returnTo)
  if (returnTo) params.set('returnTo', returnTo)
  if (options?.tenant) params.set('tenant', options.tenant)
  if (options?.ticket) params.set('ticket', options.ticket)

  const query = params.toString()
  return `${apiBase()}/auth/oauth/${provider}/start${query ? `?${query}` : ''}`
}

/**
 * Starts a sign-in. This is a full-page navigation on purpose: the provider
 * redirect and the handler's `state` correlation cookie both require a top-level
 * document request, and an XHR could not carry the user through the consent page.
 */
export function beginProviderSignIn(
  provider: OAuthProvider,
  options?: { returnTo?: string | null; tenant?: string | null }
): void {
  window.location.assign(buildSignInStartUrl(provider, options))
}

/**
 * Redeems the single-use code from the callback and adopts the session.
 *
 * The code is spent server-side on first use, so this must run exactly once per
 * callback — the callback page guards re-entry.
 */
export async function exchangeOAuthCode(code: string): Promise<OAuthExchangeResult> {
  const response = await fetch(`${apiBase()}/auth/oauth/exchange`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ code }),
  })

  const body = await response.json().catch(() => null)
  if (!response.ok) {
    throw new Error(
      body?.message ?? body?.Message ?? 'This sign-in link is invalid or has expired.'
    )
  }

  const result = body?.result ?? body?.Result
  if (!result?.token) throw new Error('The sign-in response did not contain a session.')

  await adoptSession(result.token, result.userId)

  return {
    token: result.token,
    userId: result.userId,
    username: result.username,
    email: result.email,
    provider: result.provider,
    returnTo: safeReturnTo(result.returnTo),
  }
}

/**
 * Stores the token under the SDK's session keys and reloads it into the live
 * client, so a federated session behaves exactly like a password one.
 */
export async function adoptSession(token: string, userId?: string): Promise<void> {
  if (typeof window !== 'undefined') {
    window.localStorage.setItem(SESSION_TOKEN_KEY, token)
    if (userId) window.localStorage.setItem(SESSION_USER_KEY, userId)
  }
  await azoa.session.restore()
}

/**
 * Attaches a provider to the ALREADY-AUTHENTICATED user.
 *
 * Two steps by necessity: the redirect that follows is a top-level navigation and
 * carries no Authorization header, so the authenticated call happens first and
 * mints a single-use ticket that the redirect carries instead. This is the
 * "authenticated session plus explicit user action" rule — an OAuth identity is
 * never attached to an existing account any other way.
 */
export async function beginProviderLink(
  provider: OAuthProvider,
  options?: { tenant?: string | null; returnTo?: string | null }
): Promise<void> {
  const token = azoa.session.token
  if (!token) throw new Error('Sign in before linking a provider.')

  const params = new URLSearchParams()
  if (options?.tenant) params.set('tenant', options.tenant)
  const query = params.toString()

  const response = await fetch(
    `${apiBase()}/auth/oauth/${provider}/link/start${query ? `?${query}` : ''}`,
    {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
    }
  )

  const body = await response.json().catch(() => null)
  if (!response.ok) {
    throw new Error(body?.message ?? body?.Message ?? 'Could not start the linking flow.')
  }

  const ticket = (body?.result ?? body?.Result)?.ticket
  if (!ticket) throw new Error('Could not start the linking flow.')

  window.location.assign(
    buildSignInStartUrl(provider, { ticket, tenant: options?.tenant, returnTo: options?.returnTo })
  )
}

/** Providers currently attached to the authenticated user. */
export async function listLinkedProviders(): Promise<LinkedProvider[]> {
  const token = azoa.session.token
  if (!token) return []

  const response = await fetch(`${apiBase()}/auth/oauth/links`, {
    headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
  })
  if (!response.ok) return []

  const body = await response.json().catch(() => null)
  const result = body?.result ?? body?.Result
  return Array.isArray(result) ? result : []
}

/** Detaches a provider. The backend refuses if it is the last way into the account. */
export async function unlinkProvider(
  provider: OAuthProvider,
  options?: { tenant?: string | null }
): Promise<void> {
  const token = azoa.session.token
  if (!token) throw new Error('Sign in first.')

  const params = new URLSearchParams()
  if (options?.tenant) params.set('tenant', options.tenant)
  const query = params.toString()

  const response = await fetch(
    `${apiBase()}/auth/oauth/links/${provider}${query ? `?${query}` : ''}`,
    {
      method: 'DELETE',
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
    }
  )

  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.message ?? body?.Message ?? 'Could not unlink that provider.')
  }
}
