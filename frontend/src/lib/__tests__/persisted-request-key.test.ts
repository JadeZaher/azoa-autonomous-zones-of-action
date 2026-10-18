import { beforeEach, describe, expect, it } from 'vitest'
import { claimRequestKey, clearSavedRequest, isDefiniteRefusal, isFinishedCheckout, readSavedRequest, rememberServerId } from '../persisted-request-key'

beforeEach(() => localStorage.clear())

describe('claimRequestKey', () => {
  it('reuses the key for the same amount and never replaces an unresolved key for another amount', () => {
    const first = claimRequestKey('cash-out', 'user', 50_00)
    if (first.kind !== 'ready') throw new Error('expected a fresh claim')
    expect(claimRequestKey('cash-out', 'user', 50_00)).toEqual(first)
    expect(claimRequestKey('cash-out', 'user', 49_00)).toEqual({ kind: 'pending', pending: first.request })
    expect(readSavedRequest('cash-out', 'user')).toEqual(first.request)
  })

  it('issues a fresh key once the saved one is cleared, and keeps scopes and users apart', () => {
    const first = claimRequestKey('funds', 'user', 25_00)
    clearSavedRequest('funds', 'user')
    const next = claimRequestKey('funds', 'user', 25_00)
    if (first.kind !== 'ready' || next.kind !== 'ready') throw new Error('expected fresh claims')
    expect(next.request.requestKey).not.toBe(first.request.requestKey)
    expect(claimRequestKey('funds', 'other-user', 10_00).kind).toBe('ready')
    expect(claimRequestKey('cash-out', 'user', 10_00).kind).toBe('ready')
  })

  it('remembers the server id only for the key it answered', () => {
    const claim = claimRequestKey('funds', 'user', 25_00)
    if (claim.kind !== 'ready') throw new Error('expected a fresh claim')
    rememberServerId('funds', 'user', 'some-other-key', 'intent-x')
    expect(readSavedRequest('funds', 'user')?.serverId).toBeUndefined()
    rememberServerId('funds', 'user', claim.request.requestKey, 'intent-1')
    expect(readSavedRequest('funds', 'user')?.serverId).toBe('intent-1')
  })
})

describe('key resolution rules', () => {
  it.each([
    [{ status: 'awaiting_payment', settlementState: 'not_started' }, false],
    [{ status: 'processing', settlementState: 'payment_received' }, false],
    [{ status: 'complete', settlementState: 'payment_received' }, true],
    [{ status: 'complete', settlementState: 'funds_applied' }, true],
    [{ status: 'failed', settlementState: 'payment_failed' }, true],
    [{ status: 'expired', settlementState: 'not_started' }, true],
  ])('isFinishedCheckout(%o) = %s', (intent, finished) => {
    expect(isFinishedCheckout(intent)).toBe(finished)
  })

  it.each([
    [400, true], [403, true], [404, true], [422, true],
    [undefined, false], [401, false], [409, false], [429, false], [500, false], [503, false],
  ])('isDefiniteRefusal(%s) = %s', (status, definite) => {
    expect(isDefiniteRefusal(status)).toBe(definite)
  })
})
