import { http, HttpResponse, type HttpHandler } from 'msw'
import type { TenantKycProviderChoiceResponse, TenantKycSelectionResponse } from '@/lib/operator-contracts'
import { apiUrl, azoaError, azoaOk } from '../api'
import { fresh } from '../fixtures'
import { requireBearer } from './auth'

/** A fake of `TenantKycConfigurationController` with the tenant's provider selection as state. */
export interface TenantKycApi {
  handlers: HttpHandler[]
  readonly selection: TenantKycSelectionResponse
  /** The next PUT fails the `RecentFirstPartyLogin` policy, as a session older than ten minutes does. */
  requireRecentLogin(): void
}

/**
 * Bodies follow `Managers/KycControlPlaneManager.cs`; the step-up refusal is the exact body
 * `Services/Auth/ActionableAuthorizationResultHandler.cs` writes.
 */
export function tenantKycApi(options: {
  choices: TenantKycProviderChoiceResponse[]
  selection: TenantKycSelectionResponse
}): TenantKycApi {
  let selection = fresh(options.selection)
  let staleSession = false

  const handlers = [
    http.get(apiUrl('/api/tenant/kyc/providers'), ({ request }) =>
      requireBearer(request) ?? HttpResponse.json(azoaOk(fresh(options.choices))),
    ),
    http.get(apiUrl('/api/tenant/kyc/provider'), ({ request }) =>
      requireBearer(request) ?? HttpResponse.json(azoaOk(selection)),
    ),
    http.put(apiUrl('/api/tenant/kyc/provider'), async ({ request }) => {
      const denied = requireBearer(request)
      if (denied) return denied
      if (staleSession) {
        staleSession = false
        return HttpResponse.json(
          { isError: true, code: 'RECENT_LOGIN_REQUIRED', message: 'Sign in again to confirm this sensitive account action.' },
          { status: 403, headers: { 'X-AZOA-Auth-Requirement': 'recent-first-party-login' } },
        )
      }
      const body = (await request.json()) as { providerKey: string; expectedVersion?: number | null }
      if ((body.expectedVersion ?? 0) !== selection.selectionVersion)
        return HttpResponse.json(azoaError('Tenant KYC provider selection version conflict.', 'CONFLICT'), { status: 409 })
      const choice = options.choices.find((candidate) => candidate.providerKey === body.providerKey)
      if (!choice) return HttpResponse.json(azoaError('PROFILE_NOT_CONFIGURED', 'POLICY_UNAVAILABLE'), { status: 400 })
      selection = {
        ...selection,
        providerKey: choice.providerKey,
        providerDisplayName: choice.displayName,
        selectionVersion: selection.selectionVersion + 1,
        readinessCode: 'READY',
        updatedAt: new Date().toISOString(),
      }
      return HttpResponse.json(azoaOk(selection))
    }),
  ]

  return {
    handlers,
    get selection() {
      return selection
    },
    requireRecentLogin() {
      staleSession = true
    },
  }
}
