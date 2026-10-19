import { afterEach, describe, expect, it, vi } from 'vitest'
import { AzoaApiClient } from '../../src/api/client.js'

afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers() })

describe('workflow designer transport privacy and deadline', () => {
  it.each(['/api/action-designer/interview', '/api/action-designer/interview?trace=1', '/API/Action-Designer/Interview/'])('omits the entire interview body from debug logs for %s', async (path) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ result: { summary: 'Review' } }), { status: 200 })))
    const logger = { debug: vi.fn(), error: vi.fn() }
    const client = new AzoaApiClient({ baseUrl: 'https://node.example', token: 'test-token', debug: true, debugLogger: logger })
    await client.request('POST', path, { intake: { purpose: 'confidential business idea' }, messages: [] })
    expect(JSON.stringify(logger.debug.mock.calls)).not.toContain('confidential business idea')
    expect(JSON.stringify(logger.debug.mock.calls)).toContain('private AI interview omitted')
  })

  it('allows the bounded AI request to outlive the normal thirty-second timeout', async () => {
    vi.useFakeTimers()
    let signal!: AbortSignal
    let finish!: (response: Response) => void
    vi.stubGlobal('fetch', vi.fn((_url: string, options: RequestInit) => {
      signal = options.signal!
      return new Promise<Response>(resolve => { finish = resolve })
    }))
    const client = new AzoaApiClient({ baseUrl: 'https://node.example', token: 'test-token' })
    const pending = client.request('POST', '/api/action-designer/interview', { intake: {} })
    await vi.advanceTimersByTimeAsync(31000)
    expect(signal.aborted).toBe(false)
    await vi.advanceTimersByTimeAsync(64000)
    expect(signal.aborted).toBe(true)
    finish(new Response(JSON.stringify({ result: {} }), { status: 200 }))
    await pending
  })
})
