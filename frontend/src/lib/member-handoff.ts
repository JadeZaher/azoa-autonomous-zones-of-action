import { azoa } from './azoa'

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** Captures the invitation once and removes it from browser history before any network call. */
export function takeHandoffFragment(): { requestId: string; targetUserId: string; token: string } | null {
  const fragment = new URLSearchParams(window.location.hash.slice(1))
  window.history.replaceState(null, '', window.location.pathname)
  const requestId = fragment.get('requestId') ?? ''
  const targetUserId = fragment.get('targetUserId') ?? ''
  const token = fragment.get('token') ?? ''
  return UUID.test(requestId) && (!targetUserId || UUID.test(targetUserId)) && token.length <= 512
    ? { requestId, targetUserId, token } : null
}

/** Restores the already-chosen account credential after an uncertain claim response. */
export async function recoverClaimLogin(targetUserId: string, password: string): Promise<boolean> {
  const login = await azoa.api.loginClaimedMember(targetUserId, password)
  if (!login.ok) return false
  await azoa.session.acceptAuthenticationToken(login.value, targetUserId)
  return true
}
