import { beforeEach, expect, it, vi } from 'vitest'
import { NextRequest, NextResponse } from 'next/server'
import { GET, POST } from './route'
const bridge = vi.hoisted(() => ({ upstream: vi.fn(), origin: vi.fn() }))
vi.mock('next/headers', () => ({ cookies: async () => ({ get: () => ({ value: 'operator-cookie' }) }) }))
vi.mock('@/lib/operator-bff', () => ({ OPERATOR_COOKIE_NAME: 'operator', operatorUpstreamFetch: bridge.upstream,
  requireSameOriginMutation: bridge.origin, readSmallJson: (request: Request) => request.json(), sanitizedOperatorResponse: (response: Response) => response }))
const definition = { chainType: 'Algorand', name: 'Arda', unitName: 'ARDA', total: '18446744073709551615', decimals: 2, expectedNetwork: 'Devnet', confirmIrreversible: true }
const send = (path: string[], body: unknown) => POST(new NextRequest('https://console.example/api/operator/' + path.join('/'),
  { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }), { params: Promise.resolve({ path }) })
beforeEach(() => { vi.resetAllMocks(); bridge.upstream.mockResolvedValue(NextResponse.json({ result: {} })) })
it('preserves exact integer supply and strips caller-supplied signing authority', async () => {
  await send(['platform-token', 'genesis'], { ...definition, authenticationWitness: { ownerAuthority: true } })
  expect(bridge.upstream).toHaveBeenCalledWith('platform-token/genesis', 'operator-cookie', { method: 'POST', body: definition })
})
it('rejects unsupported genesis cancellation and malformed consent without egress', async () => {
  expect((await send(['platform-token', 'creation', 'a'.repeat(32), 'cancel'], {})).status).toBe(404)
  expect((await send(['platform-token', 'genesis'], { ...definition, total: 18446744073709551615 })).status).toBe(400)
  expect((await send(['platform-token', 'genesis'], { ...definition, confirmIrreversible: false })).status).toBe(400)
  expect(bridge.upstream).not.toHaveBeenCalled()
})
it('permits only exact recovery verbs and safe expected versions', async () => {
  const id = 'a'.repeat(32)
  const body = { requestId: '11111111-1111-1111-1111-111111111111', version: 7 }
  await send(['platform-token', 'creation', id, 'continue'], body)
  expect(bridge.upstream).toHaveBeenCalledWith(`platform-token/creation/${id}/continue`, 'operator-cookie', { method: 'POST', body })
  expect((await send(['platform-token', 'creation', id, 'replace'], { ...body, version: Number.MAX_SAFE_INTEGER + 1 })).status).toBe(400)
  const read = new NextRequest(`https://console.example/api/operator/platform-token/creation/${id}?actor=forged`)
  expect((await GET(read, { params: Promise.resolve({ path: ['platform-token', 'creation', id] }) })).status).toBe(404)
})
