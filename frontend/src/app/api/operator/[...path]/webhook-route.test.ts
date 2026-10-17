import { beforeEach, expect, it, vi } from 'vitest'
import { NextRequest, NextResponse } from 'next/server'
import { GET, PUT, POST } from './route'

const bridge = vi.hoisted(() => ({ upstream: vi.fn(), origin: vi.fn() }))
vi.mock('next/headers', () => ({ cookies: async () => ({ get: () => ({ value: 'operator-cookie-fixture' }) }) }))
vi.mock('@/lib/operator-bff', () => ({
  OPERATOR_COOKIE_NAME: 'operator', operatorUpstreamFetch: bridge.upstream,
  requireSameOriginMutation: bridge.origin,
  readSmallJson: (request: Request) => request.json(),
  sanitizedOperatorResponse: (response: Response) => response,
}))
const tenant = '11111111-1111-1111-1111-111111111111'
const path = ['tenants', tenant, 'webhook-registration']
const command = { requestId: '22222222-2222-2222-2222-222222222222', registrationId: '33333333-3333-3333-3333-333333333333',
  expectedVersion: 4, url: 'https://receiver.example/hook', isActive: false, rotateSecret: false,
  reason: 'Disable this receiver endpoint.', replaceLegacy: false, legacyFingerprint: null }
const request = (method: string, body?: unknown) => new NextRequest('https://console.example/api/operator/' + path.join('/'),
  { method, ...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}) })
beforeEach(() => { vi.resetAllMocks(); bridge.upstream.mockResolvedValue(NextResponse.json({ result: null }, { headers: { 'Cache-Control': 'no-store' } })) })

it.each(['webhook-events', 'consent-webhook-events'])('restricts %s paging and strips retry authority overrides', async family => {
  const events = ['tenants', tenant, family]
  const url = 'https://console.example/api/operator/' + events.join('/')
  await GET(new NextRequest(`${url}?offset=25&limit=25`), { params: Promise.resolve({ path: events }) })
  expect(bridge.upstream).toHaveBeenCalledWith(events.join('/') + '?offset=25&limit=25', 'operator-cookie-fixture', { method: 'GET', body: undefined })
  const retryPath = [...events, tenant, 'retry']
  const retry = { requestId: tenant, expectedVersion: 3, reason: 'Receiver restored and verified.' }
  await POST(new NextRequest(url + '/' + tenant + '/retry', { method: 'POST', body: JSON.stringify({ ...retry, destinationRevision: 99, actorId: tenant }) }),
    { params: Promise.resolve({ path: retryPath }) })
  expect(bridge.upstream).toHaveBeenLastCalledWith(retryPath.join('/'), 'operator-cookie-fixture', { method: 'POST', body: retry })
  for (const query of ['offset=-1', 'limit=101', 'limit=1&limit=2', 'secret=true']) {
    expect((await GET(new NextRequest(`${url}?${query}`), { params: Promise.resolve({ path: events }) })).status).toBe(404)
  }
  expect(bridge.upstream).toHaveBeenCalledTimes(2)
})

it('forwards exact registration fields and strips untrusted authority or secret fields', async () => {
  await PUT(request('PUT', { ...command, tenantId: 'another-tenant', secret: 'injected', authority: { owner: true } }), { params: Promise.resolve({ path }) })
  expect(bridge.upstream).toHaveBeenCalledWith(path.join('/'), 'operator-cookie-fixture', { method: 'PUT', body: command })
})

it('allows exact withdrawal and read without exposing arbitrary operator paths', async () => {
  await POST(request('POST', command), { params: Promise.resolve({ path: [...path, 'withdraw'] }) })
  expect(bridge.upstream).toHaveBeenCalledWith(path.join('/') + '/withdraw', 'operator-cookie-fixture', { method: 'POST', body: command })
  await GET(request('GET'), { params: Promise.resolve({ path }) })
  const rejected = await GET(request('GET'), { params: Promise.resolve({ path: [...path, 'secret'] }) })
  expect(rejected.status).toBe(404)
  expect(bridge.upstream).toHaveBeenCalledTimes(2)
})

it('refuses unsafe versions and preserves the same-origin mutation gate', async () => {
  const invalid = await PUT(request('PUT', { ...command, expectedVersion: Number.MAX_SAFE_INTEGER + 1 }), { params: Promise.resolve({ path }) })
  expect(invalid.status).toBe(400)
  bridge.origin.mockReturnValue(NextResponse.json({ message: 'Forbidden' }, { status: 403 }))
  const forbidden = await POST(request('POST', command), { params: Promise.resolve({ path: [...path, 'withdraw'] }) })
  expect(forbidden.status).toBe(403)
  expect(bridge.upstream).not.toHaveBeenCalled()
})
