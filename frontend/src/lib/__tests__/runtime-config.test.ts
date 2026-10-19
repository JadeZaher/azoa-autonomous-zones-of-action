import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { GET as getHealth } from '../../app/api/health/route'
import { operatorUpstreamFetch } from '../operator-bff'
import { readInitialApiUrl, resolveInternalApiUrl, resolveServerApiUrl } from '../runtime-config'

describe('separate browser and server API origins', () => {
  beforeEach(() => {
    vi.stubEnv('NODE_ENV', 'production')
    vi.stubEnv('API_URL', 'https://api.example.test')
    vi.stubEnv('INTERNAL_API_URL', '')
    vi.stubEnv('AZOA_ALLOW_INSECURE_LOCAL_API', '')
  })

  afterEach(() => {
    vi.unstubAllEnvs()
    vi.unstubAllGlobals()
  })

  it('keeps an internal container origin out of public and browser runtime configuration', () => {
    vi.stubEnv('INTERNAL_API_URL', 'http://azoa-api:5000')
    vi.stubEnv('AZOA_ALLOW_INSECURE_LOCAL_API', 'true')
    vi.stubGlobal('window', { __RUNTIME_CONFIG__: { apiUrl: 'https://api.example.test' } })

    expect(resolveInternalApiUrl()).toBe('http://azoa-api:5000')
    expect(resolveServerApiUrl()).toBe('https://api.example.test')
    expect(readInitialApiUrl()).toBe('https://api.example.test')
  })

  it('keeps SSR SDK configuration on the public origin', () => {
    vi.stubEnv('INTERNAL_API_URL', 'http://azoa-api:5000')
    vi.stubEnv('AZOA_ALLOW_INSECURE_LOCAL_API', 'true')
    vi.stubGlobal('window', undefined)

    expect(readInitialApiUrl()).toBe('https://api.example.test')
  })

  it('uses the public origin for server calls when no internal origin is configured', () => {
    expect(resolveInternalApiUrl()).toBe('https://api.example.test')
  })

  it.each(['http://azoa-api:5000', 'http://127.0.0.1:5200', 'https://azoa-api:5000', 'http://api.example.test'])(
    'preserves production refusal for %s without the local opt-in', (origin) => {
      vi.stubEnv('INTERNAL_API_URL', origin)
      expect(() => resolveInternalApiUrl()).toThrow('Production INTERNAL_API_URL must be a public HTTPS origin.')
    },
  )

  it.each(['https://user:pass@api.example.test', 'https://api.example.test/path', 'https://api.example.test/?q=1', 'https://api.example.test/#hash', 'ftp://api.example.test'])(
    'refuses a malformed or credential-bearing internal origin: %s', (origin) => {
      vi.stubEnv('INTERNAL_API_URL', origin)
      expect(() => resolveInternalApiUrl()).toThrow('INTERNAL_API_URL must be a credential-free HTTP(S) origin.')
    },
  )

  it('routes frontend health and operator BFF requests through the internal origin', async () => {
    vi.stubEnv('INTERNAL_API_URL', 'http://azoa-api:5000')
    vi.stubEnv('AZOA_ALLOW_INSECURE_LOCAL_API', 'true')
    const fetchMock = vi.fn<typeof fetch>(async () => new Response('{}', { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)

    const health = await getHealth()
    await operatorUpstreamFetch('overview', 'test-session')

    expect(health.status).toBe(200)
    expect(await health.json()).toMatchObject({ status: 'ready', checks: { api: { ready: true } } })
    expect(fetchMock.mock.calls.map(([url]) => String(url))).toEqual([
      'http://azoa-api:5000/health',
      'http://azoa-api:5000/api/operator/overview',
    ])
  })
})
