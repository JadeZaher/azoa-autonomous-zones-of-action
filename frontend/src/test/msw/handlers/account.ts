import { http, HttpResponse, type HttpHandler } from 'msw'
import { apiUrl, azoaError, azoaOk } from '../api'
import { requireBearer } from './auth'

/** `UserController` POST `/api/user/login`: one accepted credential pair, anything else is 401 "Invalid credentials.". */
export function loginApi(credentials: { email: string; password: string; token: string }): { handlers: HttpHandler[] } {
  return {
    handlers: [
      http.post(apiUrl('/api/user/login'), async ({ request }) => {
        const body = (await request.json()) as { email?: string; password?: string }
        return body.email === credentials.email && body.password === credentials.password
          ? HttpResponse.json(azoaOk(credentials.token))
          : HttpResponse.json(azoaError('Invalid credentials.'), { status: 401 })
      }),
      // `OAuthController` provider discovery: a node with no provider credentials answers an empty list.
      http.get(apiUrl('/auth/oauth/providers'), () => HttpResponse.json(azoaOk([]))),
    ],
  }
}

/** Wire shape of `ApiKeyController.ApiKeyInfo`: `scopes` is the stored comma-separated string, or null for legacy full access. */
export interface ApiKeyWireRow {
  id: string
  name: string
  keyPrefix: string
  createdDate: string
  expiresAt: string | null
  lastUsedAt: string | null
  revokedAt: string | null
  isActive: boolean
  scopes: string | null
  allowedOrigins: string | null
}

export interface ApiKeysApi {
  handlers: HttpHandler[]
  readonly rows: ReadonlyArray<ApiKeyWireRow>
}

/** A fake of `ApiKeyController` list / scopes / revoke for the caller's own keys. */
export function apiKeysApi(initial: ApiKeyWireRow[]): ApiKeysApi {
  const rows = initial.map(row => ({ ...row }))
  const handlers = [
    http.get(apiUrl('/api/apikey'), ({ request }) => requireBearer(request) ?? HttpResponse.json(azoaOk(rows, 'OK'))),
    http.get(apiUrl('/api/apikey/scopes'), ({ request }) =>
      requireBearer(request) ?? HttpResponse.json(azoaOk([{ scope: 'wallet:manage', description: 'Manage wallets', isSelfIssuable: true }], 'OK'))),
    http.post(apiUrl('/api/apikey/:id/revoke'), ({ request, params }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const row = rows.find(candidate => candidate.id === params.id)
      if (!row) return HttpResponse.json(azoaError('API key not found.'), { status: 404 })
      row.isActive = false
      row.revokedAt = '2026-10-18T06:00:00Z'
      return HttpResponse.json({ isError: false, message: 'API key revoked.', result: null, code: null, retryAfterSeconds: null, detail: null })
    }),
  ]
  return { handlers, rows }
}

/** `FungibleCreationController` GET `/api/fungible-creation`: a BARE array of views, not an `AZOAResult`. */
export function fungibleCreationApi(views: unknown[]): { handlers: HttpHandler[] } {
  return {
    handlers: [
      http.get(apiUrl('/api/fungible-creation'), ({ request }) => requireBearer(request) ?? HttpResponse.json(views)),
    ],
  }
}

/**
 * `UserController` PUT `/api/user/{id}`: `UserUpdateValidator` refuses a username outside `UsernameRules.Pattern` with a
 * `ValidationProblemDetails` 400; `UserManager` refuses a changed name with a reserved generated prefix with an `AZOAResult` 400.
 */
export function profileUpdateApi(userId: string): { handlers: HttpHandler[]; readonly received: Record<string, unknown>[] } {
  const received: Record<string, unknown>[] = []
  const handlers = [
    http.put(apiUrl('/api/user/:id'), async ({ request, params }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      if (params.id !== userId) return HttpResponse.json(azoaError('User not found.'), { status: 404 })
      const body = (await request.json()) as Record<string, unknown>
      received.push(body)
      if (typeof body.username === 'string' && !/^[a-zA-Z0-9_-]{3,50}$/.test(body.username)) {
        return HttpResponse.json({
          type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
          title: 'One or more validation errors occurred.',
          status: 400,
          errors: { Username: ['Username must be 3-50 characters long and contain only letters, numbers, underscores, or hyphens.'] },
        }, { status: 400 })
      }
      if (typeof body.username === 'string' && /^(wallet|oauth|onboard)-/i.test(body.username))
        return HttpResponse.json(azoaError("Usernames starting with 'wallet-', 'oauth-' or 'onboard-' are reserved for generated accounts."), { status: 400 })
      return HttpResponse.json(azoaOk({ id: userId, username: body.username ?? 'slice-owner', email: 'slice-owner@example.test', isActive: true }))
    }),
  ]
  return { handlers, received }
}
