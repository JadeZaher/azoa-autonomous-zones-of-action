import { describe, expect, it } from 'vitest'
import { sanitizedOperatorResponse } from '../operator-bff'

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
