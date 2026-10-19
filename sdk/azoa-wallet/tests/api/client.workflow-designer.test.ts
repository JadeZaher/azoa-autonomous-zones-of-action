import { afterEach, describe, expect, it, vi } from 'vitest'
import { AzoaApiClient } from '../../src/api/client.js'
import type { WorkflowDesignerRequest } from '../../src/api/workflow-designer.js'

afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers() })

describe('workflow designer transport privacy and deadline', () => {
  it('preserves user amounts and explicit planning scope on the private request', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ result: {} }), { status: 200 }))
    vi.stubGlobal('fetch', fetch)
    const payload: WorkflowDesignerRequest = {
      tenantId: '3f91074f-b6db-4e81-bdbb-7370c7c6bdae', consentToProvider: true,
      jurisdictionsConfirmed: true, economicTermsConfirmed: true, planningScopeAccepted: true,
      intake: { purpose: 'Cooperative review', participants: 'Two independent reviewers', jurisdictions: 'Colorado, United States',
        valueMovement: '5000 cents, 50/50 allocation', allocationsAndTax: 'External records and adviser review',
        failureAndRefunds: 'Unknown payment state stays frozen for external investigation.' }, messages: [],
    }
    await new AzoaApiClient({ baseUrl: 'https://node.example', token: 'test-token' }).request('POST', '/api/action-designer/interview', payload)
    expect(JSON.parse(fetch.mock.calls[0][1].body)).toEqual(payload)
  })

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
