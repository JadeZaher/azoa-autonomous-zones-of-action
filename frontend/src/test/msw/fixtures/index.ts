import type {
  BusinessVerificationCapabilities,
  BusinessVerificationSession,
  BusinessVerificationStatus,
  WebhookRegistrationMutationResult,
  WebhookRegistrationStatus,
  WebhookRegistrationWithdrawal,
} from 'azoa-sdk'
import type { TenantKycProviderChoiceResponse, TenantKycSelectionResponse } from '@/lib/operator-contracts'
import businessVerification from './business-verification.json'
import tenantKyc from './tenant-kyc.json'
import webhookRegistration from './webhook-registration.json'

/**
 * What a JSON import of `T` looks like to TypeScript: literal unions widen to their primitive and
 * every key, optional ones included, must be present.
 */
type JsonShape<T> = T extends string
  ? string
  : T extends number
    ? number
    : T extends boolean
      ? boolean
      : T extends null | undefined
        ? T
        : T extends ReadonlyArray<infer Element>
          ? JsonShape<Element>[]
          : T extends object
            ? { [Key in keyof T]-?: JsonShape<T[Key]> }
            : T

/**
 * Typed builder: a fixture missing an SDK field, or carrying the wrong primitive kind, fails
 * `npm run typecheck`. Literal values (e.g. `status: "APPROVED"`) are the one thing JSON imports
 * cannot narrow; those are pinned by the backend contract test against live payloads
 * (`tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/FrontendFixtureContract.cs`).
 * See `src/test/AGENTS.md` §fixtures.
 */
function fixture<T>(json: JsonShape<T>): T {
  return json as unknown as T
}

export const businessVerificationFixtures = {
  capabilitiesAvailable: fixture<BusinessVerificationCapabilities>(businessVerification.capabilitiesAvailable),
  capabilitiesUnavailable: fixture<BusinessVerificationCapabilities>(businessVerification.capabilitiesUnavailable),
  pending: fixture<BusinessVerificationStatus>(businessVerification.pending),
  approved: fixture<BusinessVerificationStatus>(businessVerification.approved),
  sessionStarted: fixture<BusinessVerificationSession>(businessVerification.sessionStarted),
}

export const webhookRegistrationFixtures = {
  status: fixture<WebhookRegistrationStatus>(webhookRegistration.status),
  mutationResult: fixture<WebhookRegistrationMutationResult>(webhookRegistration.mutationResult),
  withdrawal: fixture<WebhookRegistrationWithdrawal>(webhookRegistration.withdrawal),
}

export const tenantKycFixtures = {
  identomatChoice: fixture<TenantKycProviderChoiceResponse>(tenantKyc.identomatChoice),
  selectedIdentomat: fixture<TenantKycSelectionResponse>(tenantKyc.selectedIdentomat),
}

/** Deep copy so a handler's state machine never mutates a shared fixture. */
export function fresh<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}
