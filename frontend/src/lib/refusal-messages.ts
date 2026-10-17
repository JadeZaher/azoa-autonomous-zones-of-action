import { isKybRequired, type SdkError } from 'azoa-sdk'

/** Shown wherever a tenant-driven or custodial action is refused for missing business verification (D-6). */
export const KYB_REQUIRED_MESSAGE =
  'This needs your organization’s business verification. Finish (or renew) it under Business verification, then try again.'

/**
 * One sentence a person can act on for an SDK refusal. The server message wins for typed refusals
 * (409/422) because it names the evidence; status-only failures get a plain fallback.
 */
export function refusalMessage(error: SdkError, fallback: string): string {
  if (isKybRequired(error)) return KYB_REQUIRED_MESSAGE
  if (error.status === 429) return 'Too many attempts right now. Wait a minute and try again.'
  if (error.status === 503) return 'The provider is unavailable at the moment. Try again shortly.'
  if (error.status === 401) return 'Your session expired. Sign in again.'
  if (error.status === 403) return 'You are not allowed to do this with the current sign-in.'
  const serverText = error.message.replace(/^[A-Z]+ \S+: /, '')
  if ((error.status === 409 || error.status === 422 || error.status === 400) && serverText && !serverText.includes('failed with HTTP')) return serverText
  return fallback
}
