import { NextRequest, NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import {
  OPERATOR_COOKIE_NAME,
  clearOperatorCookie,
  createOperatorSession,
  operatorUpstreamFetch,
  readSmallJson,
  requireSameOriginMutation,
} from '@/lib/operator-bff'

export const dynamic = 'force-dynamic'

const NO_STORE_HEADERS = { 'Cache-Control': 'no-store' }

export async function POST(request: NextRequest) {
  const rejected = requireSameOriginMutation(request)
  if (rejected) return rejected
  const body = await readSmallJson(request)
  const username = typeof body?.username === 'string' ? body.username.trim() : ''
  const password = typeof body?.password === 'string' ? body.password : ''
  if (!username || !password || username.length > 128 || password.length > 512) {
    return NextResponse.json(
      { message: 'Enter the configured operator username and password.' },
      { status: 400, headers: NO_STORE_HEADERS },
    )
  }
  return createOperatorSession(username, password)
}

export async function GET() {
  const token = (await cookies()).get(OPERATOR_COOKIE_NAME)?.value
  if (!token) {
    return NextResponse.json(
      { message: 'Operator sign-in is required.' },
      { status: 401, headers: NO_STORE_HEADERS },
    )
  }
  try {
    const upstream = await operatorUpstreamFetch('overview', token)
    if (!upstream.ok) {
      const status = [401, 403, 429, 503].includes(upstream.status) ? upstream.status : 502
      const result = NextResponse.json(
        {
          message: status === 401
            ? 'Operator sign-in is required.'
            : status === 403
              ? 'Operator access was not accepted.'
              : status === 429
                ? 'Too many operator requests. Wait a moment and try again.'
                : 'The node operator service is unavailable.',
        },
        { status, headers: NO_STORE_HEADERS },
      )
      if (upstream.status === 401) clearOperatorCookie(result)
      return result
    }
    return NextResponse.json({ authenticated: true }, { headers: NO_STORE_HEADERS })
  } catch {
    return NextResponse.json(
      { message: 'The node operator service is unavailable.' },
      { status: 503, headers: NO_STORE_HEADERS },
    )
  }
}

export async function DELETE(request: NextRequest) {
  const rejected = requireSameOriginMutation(request)
  if (rejected) return rejected
  const response = NextResponse.json({ signedOut: true }, { headers: NO_STORE_HEADERS })
  clearOperatorCookie(response)
  return response
}
