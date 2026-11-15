import { beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ loginClaimedMember: vi.fn() }))
const session = vi.hoisted(() => ({ acceptAuthenticationToken: vi.fn() }))
vi.mock('./azoa', () => ({ azoa: { api, session } }))
import { recoverClaimLogin, takeHandoffFragment } from './member-handoff'

const id = '11111111-1111-4111-8111-111111111111'
describe('hosted handoff fragment', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); vi.clearAllMocks() })
  it('removes the token before network work and keeps it out of browser storage', () => {
    window.history.replaceState(null, '', '/claim#requestId=' + id + '&targetUserId=' + id + '&token=member-secret')
    expect(takeHandoffFragment()).toEqual({ requestId: id, targetUserId: id, token: 'member-secret' })
    expect(window.location.hash).toBe('')
    expect(window.location.search).toBe('')
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
    expect(api.loginClaimedMember).not.toHaveBeenCalled()
  })
  it('scrubs invalid fragments too', () => {
    window.history.replaceState(null, '', '/claim#requestId=invalid&token=secret')
    expect(takeHandoffFragment()).toBeNull()
    expect(window.location.hash).toBe('')
  })
  it('recovers an uncertain claim with the same credential and stable identity', async () => {
    api.loginClaimedMember.mockResolvedValue({ ok: true, value: 'first-party-token' })
    await expect(recoverClaimLogin(id, 'same-password')).resolves.toBe(true)
    expect(api.loginClaimedMember).toHaveBeenCalledWith(id, 'same-password')
    expect(session.acceptAuthenticationToken).toHaveBeenCalledWith('first-party-token', id)
  })
  it('cannot establish a session on a refused password', async () => {
    api.loginClaimedMember.mockResolvedValue({ ok: false })
    await expect(recoverClaimLogin(id, 'wrong-password')).resolves.toBe(false)
    expect(session.acceptAuthenticationToken).not.toHaveBeenCalled()
  })
})
