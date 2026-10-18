import { afterEach, describe, expect, it, vi } from 'vitest'
import { NextRequest } from 'next/server'
import { requireSameOriginMutation, sanitizedOperatorResponse } from '../operator-bff'

const upstream = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
const INVALID = { message: 'The node returned an invalid operator response.' }

describe('sanitizedOperatorResponse never turns a contract mismatch into an empty result', () => {
  it.each([
    ['bare array on a bare route', [{ id: 'p' }], true, 200, { result: [{ id: 'p' }] }],
    ['bare object on a bare route', { status: 'Blocked', version: 3 }, true, 200, { result: { status: 'Blocked', version: 3 } }],
    ['envelope on an enveloped route', { isError: false, message: 'Success', result: true }, false, 200, { result: true }],
    ['bare array on an enveloped route', [{ id: 'p' }], false, 502, INVALID],
    ['bare object on an enveloped route', { status: 'Blocked', version: 3 }, false, 502, INVALID],
    ['error envelope on a 200', { isError: true, message: 'boom', result: null }, false, 502, INVALID],
  ] as const)('%s', async (_name, body, bare, status, expected) => {
    const response = await sanitizedOperatorResponse(upstream(body), { bare })
    expect(response.status).toBe(status)
    expect(await response.json()).toEqual(expected)
  })
})

describe('requireSameOriginMutation accepts the public origin and nothing else', () => {
  const INTERNAL = 'http://0.0.0.0:8080/api/operator/session'
  const PUBLIC = 'https://azoa-frontend-production.up.railway.app'
  const mutation = (headers: Record<string, string>, url = INTERNAL) =>
    new NextRequest(url, { method: 'POST', headers: { 'x-azoa-operator-request': '1', ...headers } })

  afterEach(() => vi.unstubAllEnvs())

  it.each([
    ['direct request on the same origin', 'http://localhost:3000/api/operator/session', { origin: 'http://localhost:3000', host: 'localhost:3000' }, true],
    ['proxied request via X-Forwarded-Host/Proto', INTERNAL, { origin: PUBLIC, host: '0.0.0.0:8080', 'x-forwarded-host': 'azoa-frontend-production.up.railway.app', 'x-forwarded-proto': 'https' }, true],
    ['proxied request where the proxy keeps the public Host', INTERNAL, { origin: PUBLIC, host: 'azoa-frontend-production.up.railway.app', 'x-forwarded-proto': 'https' }, true],
    ['spoofed cross-site Origin behind the proxy', INTERNAL, { origin: 'https://evil.example', 'x-forwarded-host': 'azoa-frontend-production.up.railway.app', 'x-forwarded-proto': 'https' }, false],
    ['scheme downgrade (http Origin, https proxy)', INTERNAL, { origin: 'http://azoa-frontend-production.up.railway.app', 'x-forwarded-host': 'azoa-frontend-production.up.railway.app', 'x-forwarded-proto': 'https' }, false],
    ['ambiguous comma-joined X-Forwarded-Host', INTERNAL, { origin: PUBLIC, 'x-forwarded-host': 'azoa-frontend-production.up.railway.app, evil.example', 'x-forwarded-proto': 'https' }, false],
    ['forwarded host carrying a path', INTERNAL, { origin: 'https://evil.example', 'x-forwarded-host': 'evil.example/x', 'x-forwarded-proto': 'https' }, false],
    ['internal container origin as Origin', INTERNAL, { origin: 'http://0.0.0.0:8080', 'x-forwarded-host': 'azoa-frontend-production.up.railway.app', 'x-forwarded-proto': 'https' }, false],
    ['missing Origin', INTERNAL, { 'x-forwarded-host': 'azoa-frontend-production.up.railway.app', 'x-forwarded-proto': 'https' }, false],
  ] as const)('%s', (_name, url, headers, accepted) => {
    expect(requireSameOriginMutation(mutation({ ...headers }, url)) === null).toBe(accepted)
  })

  it('refuses a correct origin without the operator intent header', () => {
    const request = new NextRequest('http://localhost:3000/api/operator/session', { method: 'POST', headers: { origin: 'http://localhost:3000', host: 'localhost:3000' } })
    expect(requireSameOriginMutation(request)?.status).toBe(403)
  })

  it('PUBLIC_ORIGIN pins the only accepted origin, ignoring forwarded headers', () => {
    vi.stubEnv('PUBLIC_ORIGIN', PUBLIC)
    expect(requireSameOriginMutation(mutation({ origin: PUBLIC }))).toBeNull()
    expect(requireSameOriginMutation(mutation({ origin: 'https://evil.example', 'x-forwarded-host': 'evil.example', 'x-forwarded-proto': 'https' }))?.status).toBe(403)
  })

  it('a malformed PUBLIC_ORIGIN refuses every mutation instead of falling back to headers', () => {
    vi.stubEnv('PUBLIC_ORIGIN', 'https://azoa.example/path')
    expect(requireSameOriginMutation(mutation({ origin: 'https://azoa.example', host: 'azoa.example', 'x-forwarded-proto': 'https' }))?.status).toBe(403)
  })
})
