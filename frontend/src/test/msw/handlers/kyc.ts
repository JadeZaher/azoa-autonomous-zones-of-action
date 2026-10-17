import { http, HttpResponse, type HttpHandler } from 'msw'
import type { KycSessionStart, KycSubmission } from 'azoa-sdk'
import { apiUrl, azoaError, azoaOk } from '../api'
import { requireBearer } from './auth'

/** A fake of first-party `KycController` (`/session`, `/status`, `/refresh`) with the caller's attempt as state. */
export interface KycApi {
  handlers: HttpHandler[]
  readonly submission: KycSubmission | null
  /** How many begin calls reached the API. */
  readonly sessionStarts: number
  /** The provider decided; the next refresh re-fetches and reports it. */
  decide(outcome: 'APPROVED' | 'REJECTED'): void
}

const VERIFICATION_URL = 'https://verify.identomat.test/session/slice'

/**
 * Follows `Controllers/KycController.cs` + `KycManager`: begin resumes an open attempt, status is
 * 404 before the first attempt, refresh re-reads the provider only when an attempt exists.
 */
export function kycApi(): KycApi {
  let submission: KycSubmission | null = null
  let decided: 'APPROVED' | 'REJECTED' | null = null
  let sessionStarts = 0

  const handlers = [
    http.get(apiUrl('/api/kyc/status'), ({ request }) =>
      requireBearer(request) ?? (submission
        ? HttpResponse.json(azoaOk(submission))
        : HttpResponse.json(azoaError('KYC submission not found.', 'NOT_FOUND'), { status: 404 }))),
    http.post(apiUrl('/api/kyc/session'), ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      sessionStarts += 1
      const now = new Date().toISOString()
      submission ??= {
        id: crypto.randomUUID(), userId: crypto.randomUUID(), tenantId: null, provider: 'IDENTOMAT', providerKey: 'identomat',
        status: 'PENDING', rejectionReason: null, providerSessionId: 'slice-session', hostedVerification: true,
        verificationUrl: VERIFICATION_URL, sessionInstructions: null, submittedAt: now, reviewedAt: null, expiresAt: null,
        createdDate: now, modifiedDate: null,
      }
      const start: KycSessionStart = {
        provider: 'IDENTOMAT', providerKey: 'identomat', hostedVerification: true, acceptsDocumentReferences: false,
        developmentSimulation: false, providerSessionId: 'slice-session', verificationUrl: VERIFICATION_URL, expiresAt: null, instructions: null,
      }
      return HttpResponse.json(azoaOk(start))
    }),
    http.post(apiUrl('/api/kyc/refresh'), ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      if (!submission) return HttpResponse.json(azoaError('KYC submission not found.', 'NOT_FOUND'), { status: 404 })
      if (decided) submission = { ...submission, status: decided, reviewedAt: new Date().toISOString(),
        rejectionReason: decided === 'REJECTED' ? 'Document unreadable' : null }
      return HttpResponse.json(azoaOk(submission))
    }),
  ]

  return {
    handlers,
    get submission() { return submission },
    get sessionStarts() { return sessionStarts },
    decide(outcome) { decided = outcome },
  }
}

export const KYC_SLICE_VERIFICATION_URL = VERIFICATION_URL
