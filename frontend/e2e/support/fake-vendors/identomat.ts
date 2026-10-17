import { randomUUID } from 'node:crypto'
import type { Server } from 'node:https'
import type { E2eEnvironment } from '../environment'
import { listenHttps, respond, type VendorRequest, type VendorResponse } from './http'

export type IdentomatVerdict = 'pending' | 'approved' | 'rejected'

/**
 * `standard` is the documented default configuration: `/begin` answers `{ id, url }` and the url's
 * `session_token` IS the id. `additional-information` (configuration with
 * `additionalInformationRequest`) adds a child `additionalSessionId` carried by the url; which token
 * the widget `/external-api/result/` accepts for that pair is undocumented, so this variant assumes
 * the url token and must be confirmed with the vendor before a journey relies on it.
 */
export type IdentomatSessionMode = 'standard' | 'additional-information'

export interface IdentomatSessionRecord {
  /** The (parent) session id: what callbacks name. */
  id: string
  /** The `session_token` the applicant-facing url carries. */
  sessionToken: string
  additionalSessionId: string | null
  clientUserId: string
  business: boolean
  verdict: IdentomatVerdict
}

/**
 * Behavioral fake of the Identomat hosts (modern `/begin` and KYB `/result`, AML screening, and
 * the legacy widget `/external-api/result/`), plus a `/__e2e/*` control plane for journeys. Shapes
 * follow the vendor documentation (`.omc/research/identomat-api-2026-10-15.md`) and mirror
 * `tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/IdentomatVendor.cs`. See `e2e/AGENTS.md`.
 */
export async function startIdentomat(environment: E2eEnvironment): Promise<Server> {
  const sessions: IdentomatSessionRecord[] = []
  const mode: IdentomatSessionMode =
    process.env.AZOA_E2E_IDENTOMAT_MODE === 'additional-information' ? 'additional-information' : 'standard'
  const origin = `https://${environment.vendorHost}:${environment.identomatPort}`

  const parse = (request: VendorRequest): Record<string, unknown> => {
    try {
      return JSON.parse(request.body) as Record<string, unknown>
    } catch {
      return {}
    }
  }

  const handle = (request: VendorRequest): VendorResponse => {
    if (request.path.startsWith('/__e2e/')) return control(request)
    if (request.method !== 'POST') return respond(405)
    const body = parse(request)

    if (request.path === '/external-api/result/') {
      if (body.company_key !== environment.identomatCompanyKey) return respond(401)
      // Keyed by the session token the applicant url carries: in standard mode that is the id.
      const session = sessions.find((candidate) => !candidate.business && candidate.sessionToken === body.session_token)
      if (!session) return respond(200, { result: null })
      if (session.verdict === 'approved')
        return respond(200, {
          result: 'approved',
          person: {
            first_name: 'Journey',
            last_name: 'Member',
            birthday: '1991-02-03',
            citizenship: 'US',
            document_number: `D${session.id.slice(-8)}`,
            issuing_state: 'US',
          },
        })
      if (session.verdict === 'rejected')
        return respond(200, { result: 'rejected', reject_reason: { value: 'DOCUMENT_EXPIRED', description: 'Document expired' } })
      return respond(200, { result: 'MANUAL_CHECK' })
    }

    if (body.companyKey !== environment.identomatCompanyKey) return respond(401)
    switch (request.path) {
      case '/begin': {
        const business = body.configId === environment.identomatKybConfig
        if (!business && body.configId !== environment.identomatKycConfig) return respond(200, { argumentError: 'unknown configId' })
        const id = `idm-session-${randomUUID()}`
        const child = mode === 'additional-information' ? `idm-child-${randomUUID()}` : null
        const session: IdentomatSessionRecord = {
          id,
          sessionToken: child ?? id,
          additionalSessionId: child,
          clientUserId: String(body.clientUserId ?? ''),
          business,
          verdict: 'pending',
        }
        sessions.push(session)
        const url = `${origin}/widget/?session_token=${session.sessionToken}`
        return respond(200, child ? { id, url, additionalSessionId: child, additionalSessionUrl: url } : { id, url })
      }
      case '/result': {
        const session = sessions.find((candidate) => candidate.business && candidate.id === body.sessionId)
        if (!session) return respond(200, { argumentError: 'session not found' })
        const review = session.verdict === 'approved' ? 'approved' : session.verdict === 'rejected' ? 'rejected' : 'in-review'
        return respond(200, {
          result: {
            stepsResults: [
              {
                type: 'company',
                status: 'approved',
                result: { companyName: 'Journey Holdings', registrationNumber: 'JH-0001', registrationCountry: 'US' },
              },
              {
                type: 'beneficiaries',
                status: 'approved',
                result: [
                  {
                    entityType: 'individual',
                    positions: ['director'],
                    _id: `participant-${session.id}`,
                    sessionIds: [`beneficiary-${session.id}`],
                  },
                ],
              },
              { type: 'kyb-review', status: review },
            ],
          },
        })
      }
      case '/search-screening-person':
        return respond(200, { result: [] })
      case '/monitor-screening-query':
        return respond(200, { result: `monitor-${randomUUID()}` })
      case '/remove-screening-query-from-monitoring':
        return respond(200, { result: true })
      case '/list-screening-monitoring-records':
        return respond(200, { result: { start: 0, limit: 100, total: 0, data: [] } })
      default:
        return respond(404)
    }
  }

  /** Test-runner control plane: list sessions, set a verdict. Never called by the API. */
  const control = (request: VendorRequest): VendorResponse => {
    if (request.method === 'GET' && request.path === '/__e2e/sessions') return respond(200, sessions)
    const verdictRoute = /^\/__e2e\/sessions\/([^/]+)\/verdict$/.exec(request.path)
    if (request.method === 'POST' && verdictRoute) {
      const session = sessions.find((candidate) => candidate.id === verdictRoute[1] || candidate.sessionToken === verdictRoute[1])
      if (!session) return respond(404)
      session.verdict = (parse(request).verdict as IdentomatVerdict) ?? 'pending'
      return respond(200, session)
    }
    return respond(404)
  }

  return listenHttps(environment.identomatPort, environment.generatedDirectory, handle)
}
