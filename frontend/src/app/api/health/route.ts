import { NextResponse } from 'next/server'
import { resolveServerApiUrl } from '@/lib/runtime-config'

export const dynamic = 'force-dynamic'

const UPSTREAM_TIMEOUT_MS = 3_000

const responseHeaders = { 'Cache-Control': 'no-store' }

export async function GET() {
  try {
    const apiUrl = resolveServerApiUrl()
    const response = await fetch(new URL('/health', apiUrl), {
      cache: 'no-store',
      headers: { Accept: 'application/json' },
      redirect: 'error',
      signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
    })

    if (!response.ok) {
      throw new Error('API readiness probe failed')
    }

    return NextResponse.json(
      {
        status: 'ready',
        service: 'azoa-frontend',
        checks: { api: { ready: true } },
      },
      { headers: responseHeaders },
    )
  } catch {
    return NextResponse.json(
      {
        status: 'not_ready',
        service: 'azoa-frontend',
        checks: { api: { ready: false } },
      },
      { status: 503, headers: responseHeaders },
    )
  }
}
