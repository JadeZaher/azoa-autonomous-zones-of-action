import { http, HttpResponse, type HttpHandler } from 'msw'
import type { UserResponse } from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { requireBearer } from './auth'

/** What `AzoaAuthProvider` reads on mount: the signed-in user's profile and wallets. */
export interface SessionApi {
  handlers: HttpHandler[]
  readonly user: UserResponse
}

export interface SessionApiWallet {
  id: string
  chainType: string
  address: string
  isDefault: boolean
  walletType: string
}

/**
 * Follows `UserController` GET `/api/user/{id}` (owner only; another id is 404 as the IDOR rule
 * answers) and `WalletController` GET `/api/wallet?userId=` (the caller's own wallets).
 */
export function sessionApi(userId: string, wallets: SessionApiWallet[] = []): SessionApi {
  const user: UserResponse = { id: userId, username: 'slice-owner', email: 'slice-owner@example.test', isActive: true }
  const handlers = [
    http.get(apiUrl('/api/user/:id'), ({ request, params }) =>
      requireBearer(request) ??
      (params.id === userId ? HttpResponse.json(azoaOk(user)) : HttpResponse.json(azoaError('User not found.', 'NOT_FOUND'), { status: 404 }))),
    http.get(apiUrl('/api/wallet'), ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      return new URL(request.url).searchParams.get('userId') === userId
        ? HttpResponse.json(azoaOk(wallets))
        : HttpResponse.json(azoaError('Forbidden.', 'FORBIDDEN'), { status: 403 })
    }),
  ]
  return { handlers, user }
}
