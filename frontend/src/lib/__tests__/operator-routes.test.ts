import { describe, expect, it } from 'vitest'
import { resolveConsoleRoute } from '../operator-routes'

const uuid = '0f0e0d0c-0b0a-4908-8706-050403020100'
const hex32 = 'ab'.repeat(16)
const hex64 = 'cd'.repeat(32)
const q = (query = '') => new URLSearchParams(query)

describe('round-4 operator allowlist admits only the narrow action each page needs', () => {
  it.each([
    ['stuck ops, clamped limit', 'blockchain-operations/stuck', 'GET', '?limit=200', null, '/api/operator/blockchain-operations/stuck?limit=200'],
    ['retired rows map outside /api/operator', 'exchange/retired-rows', 'GET', '', null, '/api/exchange/operator/retired-rows?limit=50'],
    ['saga filter keeps repeated statuses', 'sagas/dead-letters', 'GET', '?status=Parked&status=DeadLettered', null, '/api/admin/sagas/dead-letters?status=Parked&status=DeadLettered&limit=100'],
    ['wtt action', `wtt/operations/${hex64}/resume-proof`, 'POST', '', {}, `/api/operator/wtt/operations/${hex64}/resume-proof`],
    ['deployment close', `deployments/${hex32}/resolve`, 'POST', '', { expectedVersion: 3, action: 'CloseUnresolvable', reason: 'checked the explorer', acknowledgeNoFurtherEffect: true }, `/api/operator/deployments/${hex32}/resolve`],
    ['fee schedule', 'node-governance/fee-schedule', 'PUT', '', { expectedVersion: 2, mint: { flatBaseUnits: '1000', bps: 5 } }, '/api/node-governance/fee-schedule'],
  ] as const)('%s', (_name, path, method, query, body, upstream) => {
    const resolved = resolveConsoleRoute(path, method, q(query), body ? { ...body } : null)
    expect(resolved).not.toBeNull()
    expect(resolved).not.toBe('invalid-body')
    if (resolved && resolved !== 'invalid-body') expect(resolved.upstreamPath).toBe(upstream)
  })

  it.each([
    ['limit above the page bound', 'blockchain-operations/stuck', 'GET', '?limit=201'],
    ['unknown query key', 'wallet-identities', 'GET', '?userId=x'],
    ['traversal in an id', `wtt/operations/../${hex64}`, 'GET', ''],
    ['uppercase swap id', `dex/operations/${hex64.toUpperCase()}/settle-review`, 'POST', ''],
    ['unknown saga status', 'sagas/dead-letters', 'GET', '?status=Completed'],
    ['method not allowlisted', 'deployments', 'POST', ''],
  ] as const)('refuses %s', (_name, path, method, query) => {
    expect(resolveConsoleRoute(path, method, q(query), {})).toBeNull()
  })

  it.each([
    ['extra field on a resolution', `blockchain-operations/${uuid}/resolve`, { reason: 'checked the chain state', status: 'Confirmed' }],
    ['close without acknowledgement', `deployments/${hex32}/resolve`, { expectedVersion: 1, action: 'CloseUnresolvable', reason: 'checked the explorer', acknowledgeNoFurtherEffect: false }],
    ['retired row outcome outside the enum', `exchange/retired-rows/${uuid}/resolve`, { outcome: 'Paid', reason: 'credited by hand' }],
    ['fee bps over 100%', 'node-governance/fee-schedule', { expectedVersion: 1, swap: { bps: 10_001 } }],
    ['ethereum sender not an address', 'ethereum/recovery/Devnet/activate', { sender: '0x123' }],
  ] as const)('rejects a body with %s', (_name, path, body) => {
    const method = path.startsWith('node-governance') ? 'PUT' : 'POST'
    expect(resolveConsoleRoute(path, method, q(), { ...body })).toBe('invalid-body')
  })
})
