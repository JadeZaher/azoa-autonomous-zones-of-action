import { http, HttpResponse, type HttpHandler } from 'msw'
import type { BusinessVerificationStatus } from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { businessVerificationFixtures as fixtures, fresh } from '../fixtures'
import { requireBearer } from './auth'

/** A fake of `TenantBusinessVerificationController` with the tenant's KYB record as state. */
export interface BusinessVerificationApi {
  handlers: HttpHandler[]
  /** What the API currently reports for the tenant. */
  readonly status: BusinessVerificationStatus | null
  /** The vendor finished its review; reads and refreshes now report the decision. */
  decide(outcome: 'APPROVED' | 'REJECTED'): void
}

/**
 * Status codes and bodies follow `Controllers/TenantBusinessVerificationController.cs` and
 * `Services/Kyb/KybService.cs`: a pending attempt is resumed for any key, a decided attempt
 * replayed with its original key is a 409, a new key opens a new attempt.
 */
export function businessVerificationApi(
  options: { available?: boolean; initial?: BusinessVerificationStatus | null } = {},
): BusinessVerificationApi {
  let status: BusinessVerificationStatus | null = options.initial === undefined ? null : fresh(options.initial)
  const attemptKeys = new Map<string, string>()

  const handlers = [
    http.get(apiUrl('/api/tenant/business-verification/capabilities'), ({ request }) =>
      requireBearer(request) ??
      HttpResponse.json(
        azoaOk(fresh(options.available === false ? fixtures.capabilitiesUnavailable : fixtures.capabilitiesAvailable)),
      ),
    ),
    http.get(apiUrl('/api/tenant/business-verification'), ({ request }) =>
      requireBearer(request) ?? HttpResponse.json(azoaOk(status)),
    ),
    http.post(apiUrl('/api/tenant/business-verification/refresh'), ({ request }) =>
      requireBearer(request) ?? HttpResponse.json(azoaOk(status)),
    ),
    http.post(apiUrl('/api/tenant/business-verification/session'), ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      const key = request.headers.get('Idempotency-Key')
      if (!key) return HttpResponse.json(azoaError('A valid Idempotency-Key is required.', 'INVALID_REQUEST'), { status: 400 })
      if (options.available === false)
        return HttpResponse.json(azoaError('Business verification is not configured.', 'DEPENDENCY_UNAVAILABLE'), { status: 503 })

      const resumed = status?.status === 'PENDING'
      if (!resumed && status && attemptKeys.get(status.attemptId) === key)
        return HttpResponse.json(
          azoaError('A different or completed business verification attempt exists. Read its status before starting another.', 'CONFLICT'),
          { status: 409 },
        )
      if (!resumed) {
        status = { ...fresh(fixtures.pending), attemptId: crypto.randomUUID() }
        attemptKeys.set(status.attemptId, key)
      }
      return HttpResponse.json(
        azoaOk({ verification: status, verificationUrl: fixtures.sessionStarted.verificationUrl, resumed }),
      )
    }),
  ]

  return {
    handlers,
    get status() {
      return status
    },
    decide(outcome) {
      if (!status) throw new Error('No business verification attempt exists to decide.')
      status =
        outcome === 'APPROVED'
          ? { ...fresh(fixtures.approved), attemptId: status.attemptId }
          : { ...status, status: 'REJECTED', verified: false, reviewedAt: new Date().toISOString() }
    },
  }
}
