import { isKybRequired, type SdkError } from 'azoa-sdk'

/** Shown wherever a tenant-driven or custodial action is refused for missing business verification (D-6). */
export const KYB_REQUIRED_MESSAGE =
  'This needs your organization’s business verification. Finish (or renew) it under Business verification, then try again.'

/** Shown when the server refuses with `RAIL_UNAVAILABLE`: the payment rail is not configured on this node. */
export const RAIL_UNAVAILABLE_MESSAGE =
  'Payments are not configured on this node, so nothing can be charged here yet. Nothing was charged. Ask the node operator to set up payments.'

/** True for the server's `RAIL_UNAVAILABLE` refusal (`FiatConsumerErrors.RailUnavailableCode`): a configuration state, not a transient outage. */
export function isRailUnavailable(error: SdkError): boolean {
  return error.serverCode === 'RAIL_UNAVAILABLE'
}

/**
 * One sentence a person can act on for an SDK refusal. The server message wins for typed refusals
 * (409/422) because it names the evidence; status-only failures get a plain fallback.
 */
export function refusalMessage(error: SdkError, fallback: string): string {
  if (isKybRequired(error)) return KYB_REQUIRED_MESSAGE
  if (isRailUnavailable(error)) return RAIL_UNAVAILABLE_MESSAGE
  // KycManager prefixes the message (`KYC_POLICY_UNAVAILABLE: …`) rather than setting a code.
  if (error.serverCode === 'KYC_POLICY_UNAVAILABLE' || (error.serverMessage ?? error.message).includes('KYC_POLICY_UNAVAILABLE'))
    return 'Identity verification is not available on this node yet. The node operator still has to finish setting it up.'
  if (error.status === 429) return 'Too many attempts right now. Wait a minute and try again.'
  if (error.status === 503) return 'The provider is unavailable at the moment. Try again shortly.'
  if (error.status === 401) return 'Your session expired. Sign in again.'
  if (error.status === 403) return 'You are not allowed to do this with the current sign-in.'
  // Host configuration key names belong in the operator console, never in end-user copy (QA L2).
  const serverText = (error.serverMessage ?? error.message.replace(/^[A-Z]+ \S+: /, '')).replace(/\s*Missing configuration:[\s\S]*$/, '').trim()
  if ((error.status === 409 || error.status === 422 || error.status === 400) && serverText && !serverText.includes('failed with HTTP')) return serverText
  return fallback
}
