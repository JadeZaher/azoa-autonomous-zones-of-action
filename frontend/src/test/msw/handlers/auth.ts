import { HttpResponse } from 'msw'

/**
 * Models `[Authorize]`: a request without a bearer token is challenged (401), as the API's JWT
 * scheme does. Returns `null` when the request may proceed.
 */
export function requireBearer(request: Request): Response | null {
  return /^Bearer \S+$/.test(request.headers.get('Authorization') ?? '')
    ? null
    : new HttpResponse(null, { status: 401, headers: { 'WWW-Authenticate': 'Bearer' } })
}
