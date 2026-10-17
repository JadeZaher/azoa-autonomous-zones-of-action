import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

/**
 * Federated sign-in client (Phase 1B, D18).
 *
 * The SDK singleton is mocked: importing the real one constructs an AzoaClient and
 * reads runtime config at module load, which a jsdom test has no business doing.
 */

const sessionMock = {
  token: null as string | null,
  restore: vi.fn(async () => {}),
}

vi.mock('@/lib/azoa', () => ({
  azoa: {
    get session() {
      return sessionMock
    },
  },
}))

vi.mock('@/lib/runtime-config', () => ({
  readInitialApiUrl: () => 'https://api.example.test',
  resolveServerApiUrl: () => 'https://api.example.test',
}))

import {
  buildSignInStartUrl,
  exchangeOAuthCode,
  fetchEnabledProviders,
  beginProviderLink,
  safeReturnTo,
} from '@/lib/oauth'

describe('safeReturnTo', () => {
  it('keeps a same-site relative path', () => {
    expect(safeReturnTo('/overview')).toBe('/overview')
  })

  const unsafeReturnTargets: Array<[string | null, string]> = [
    ['https://evil.test/steal', 'an absolute URL'],
    ['//evil.test/steal', 'a protocol-relative URL'],
    ['/\\evil.test', 'a backslash-bearing path'],
    ['', 'an empty string'],
    [null, 'null'],
  ]

  it.each(unsafeReturnTargets)('rejects %s (%s)', (candidate, _description) => {
    expect(safeReturnTo(candidate)).toBeNull()
  })
})

describe('buildSignInStartUrl', () => {
  it('points at the backend start endpoint for the provider', () => {
    expect(buildSignInStartUrl('google')).toBe(
      'https://api.example.test/auth/oauth/google/start'
    )
  })

  it('carries a safe returnTo and the tenant scope', () => {
    const url = new URL(buildSignInStartUrl('github', {
      returnTo: '/overview',
      tenant: '11111111111111111111111111111111',
    }))

    expect(url.pathname).toBe('/auth/oauth/github/start')
    expect(url.searchParams.get('returnTo')).toBe('/overview')
    expect(url.searchParams.get('tenant')).toBe('11111111111111111111111111111111')
  })

  it('drops an attacker-supplied absolute returnTo', () => {
    const url = new URL(buildSignInStartUrl('google', { returnTo: 'https://evil.test' }))
    expect(url.searchParams.get('returnTo')).toBeNull()
  })

  it('never puts a PKCE verifier or client secret in the browser URL', () => {
    const url = buildSignInStartUrl('google', { returnTo: '/overview' })
    expect(url).not.toContain('code_verifier')
    expect(url).not.toContain('code_challenge')
    expect(url).not.toContain('client_secret')
  })
})

describe('fetchEnabledProviders', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('returns the providers the node advertises', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({ result: ['google', 'github'] }),
    })))

    await expect(fetchEnabledProviders()).resolves.toEqual(['google', 'github'])
  })

  it('ignores unknown provider names', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({ result: ['google', 'myspace'] }),
    })))

    await expect(fetchEnabledProviders()).resolves.toEqual(['google'])
  })

  it('offers nothing when the node cannot answer', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw new Error('network down')
    }))

    await expect(fetchEnabledProviders()).resolves.toEqual([])
  })
})

describe('exchangeOAuthCode', () => {
  beforeEach(() => {
    sessionMock.token = null
    sessionMock.restore.mockClear()
    window.localStorage.clear()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('POSTs the code and stores the returned session', async () => {
    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => ({
        result: {
          token: 'jwt-token-value',
          userId: 'user-id-value',
          username: 'oauth-user',
          email: 'oauth-user@oauth.azoa.local',
          provider: 'google',
          returnTo: '/overview',
        },
      }),
    }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await exchangeOAuthCode('single-use-code')

    // The exchange is a POST carrying only the opaque code.
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('https://api.example.test/auth/oauth/exchange')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ code: 'single-use-code' })

    // The session is persisted under the SDK's keys and reloaded into the client.
    expect(window.localStorage.getItem('azoa_token')).toBe('jwt-token-value')
    expect(window.localStorage.getItem('azoa_user_id')).toBe('user-id-value')
    expect(sessionMock.restore).toHaveBeenCalledOnce()

    expect(result.provider).toBe('google')
    expect(result.returnTo).toBe('/overview')
  })

  it('surfaces the backend message and stores nothing when the code is spent', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: false,
      json: async () => ({ message: 'This sign-in link is invalid or has expired. Start again.' }),
    })))

    await expect(exchangeOAuthCode('already-used')).rejects.toThrow(/invalid or has expired/)

    expect(window.localStorage.getItem('azoa_token')).toBeNull()
    expect(sessionMock.restore).not.toHaveBeenCalled()
  })

  it('refuses a response that carries no token', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({ result: { userId: 'x' } }),
    })))

    await expect(exchangeOAuthCode('code')).rejects.toThrow(/did not contain a session/)
    expect(window.localStorage.getItem('azoa_token')).toBeNull()
  })

  it('never sanitises an unsafe returnTo into the result', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({
      ok: true,
      json: async () => ({
        result: { token: 't', userId: 'a', provider: 'github', returnTo: 'https://evil.test' },
      }),
    })))

    const result = await exchangeOAuthCode('code')
    expect(result.returnTo).toBeNull()
  })
})

describe('beginProviderLink', () => {
  beforeEach(() => {
    sessionMock.token = null
    window.localStorage.clear()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('refuses to link without an authenticated session', async () => {
    // Rule 1: attaching a provider to an existing account requires a live session.
    await expect(beginProviderLink('google')).rejects.toThrow(/Sign in before linking/)
  })

  it('mints a single-use ticket with the bearer token before redirecting', async () => {
    sessionMock.token = 'live-jwt'

    const fetchMock = vi.fn(async () => ({
      ok: true,
      json: async () => ({ result: { ticket: 'one-time-ticket', startUrl: '/x' } }),
    }))
    vi.stubGlobal('fetch', fetchMock)

    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })

    await beginProviderLink('github')

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('https://api.example.test/auth/oauth/github/link/start')
    expect(init.method).toBe('POST')
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer live-jwt')

    // The redirect carries the ticket, never a user id.
    expect(assign).toHaveBeenCalledOnce()
    const redirect = new URL(assign.mock.calls[0][0] as string)
    expect(redirect.searchParams.get('ticket')).toBe('one-time-ticket')
    expect(redirect.searchParams.has('userId')).toBe(false)
  })
})
