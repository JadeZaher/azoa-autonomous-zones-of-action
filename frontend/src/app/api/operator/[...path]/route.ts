import { NextRequest, NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import {
  OPERATOR_COOKIE_NAME,
  operatorUpstreamFetch,
  readSmallJson,
  requireSameOriginMutation,
  sanitizedOperatorResponse,
} from '@/lib/operator-bff'

export const dynamic = 'force-dynamic'

const NO_STORE_HEADERS = { 'Cache-Control': 'no-store' }

const GET_PATHS = new Set(['overview', 'kyc/providers'])
const PROVIDER_PATH = /^kyc\/providers\/[a-z0-9][a-z0-9_-]{0,63}$/
const TENANT_PROVIDER_PATH = /^tenants\/[0-9a-f-]{36}\/kyc-provider$/i
const DECISION_PATH = /^kyc\/submissions\/[0-9a-f-]{36}\/decision$/i
const SESSION_REVOKE_PATH = /^session\/revoke$/

function resolvePath(parts: string[], request: NextRequest, method: string): string | null {
  const path = parts.join('/')
  if (method === 'GET' && GET_PATHS.has(path)) return path
  if (method === 'GET' && path === 'tenants') {
    return withPaging(path, request, true)
  }
  if (method === 'GET' && path === 'kyc/submissions') {
    const status = request.nextUrl.searchParams.get('status') ?? 'pending'
    if (!['pending', 'approved', 'rejected'].includes(status)) return null
    return withPaging(`${path}?status=${encodeURIComponent(status)}`, request, false)
  }
  if (method === 'PUT' && (PROVIDER_PATH.test(path) || TENANT_PROVIDER_PATH.test(path))) return path
  if (method === 'POST' && (DECISION_PATH.test(path) || SESSION_REVOKE_PATH.test(path))) return path
  return null
}

function withPaging(path: string, request: NextRequest, allowSearch: boolean): string | null {
  const rawLimit = request.nextUrl.searchParams.get('limit') ?? '50'
  if (!/^\d{1,3}$/.test(rawLimit)) return null
  const limit = Number(rawLimit)
  if (limit < 1 || limit > 100) return null
  const cursor = request.nextUrl.searchParams.get('cursor')?.trim()
  if (cursor && (cursor.length > 512 || !/^[A-Za-z0-9._~-]+$/.test(cursor))) return null
  const search = allowSearch ? request.nextUrl.searchParams.get('search')?.trim() : undefined
  if (search && search.length > 100) return null
  const separator = path.includes('?') ? '&' : '?'
  const query = new URLSearchParams({ limit: String(limit) })
  if (cursor) query.set('cursor', cursor)
  if (search) query.set('search', search)
  return `${path}${separator}${query.toString()}`
}

function stringField(body: Record<string, unknown>, key: string, maxLength = 256): string | undefined {
  const value = body[key]
  if (typeof value !== 'string') return undefined
  const normalized = value.trim()
  return normalized && normalized.length <= maxLength ? normalized : undefined
}

function numberField(body: Record<string, unknown>, key: string): number | undefined {
  const value = body[key]
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : undefined
}

function sanitizeMutationBody(
  path: string,
  body: Record<string, unknown>,
): Record<string, unknown> | null {
  if (PROVIDER_PATH.test(path)) {
    const displayName = stringField(body, 'displayName', 120)
    const adapterKey = stringField(body, 'adapterKey', 64)
    const policyVersion = stringField(body, 'policyVersion', 64)
    const assuranceLevel = stringField(body, 'assuranceLevel', 64)
    if (!displayName || !adapterKey || !policyVersion || !assuranceLevel || typeof body.enabled !== 'boolean') return null
    return {
      displayName,
      adapterKey,
      policyVersion,
      assuranceLevel,
      enabled: body.enabled,
      expectedVersion: numberField(body, 'expectedVersion'),
    }
  }
  if (TENANT_PROVIDER_PATH.test(path)) {
    const providerKey = stringField(body, 'providerKey', 64)
    if (!providerKey) return null
    return { providerKey, expectedVersion: numberField(body, 'expectedVersion') }
  }
  if (DECISION_PATH.test(path)) {
    const decision = body.decision
    const notes = stringField(body, 'notes', 2_000)
    const reason = stringField(body, 'reason', 1_000)
    if (decision !== 'approve' && decision !== 'reject') return null
    if (decision === 'reject' && !reason) return null
    return { decision, notes, reason }
  }
  if (SESSION_REVOKE_PATH.test(path)) {
    return Object.keys(body).length === 0 ? {} : null
  }
  return null
}

async function proxy(
  request: NextRequest,
  context: { params: Promise<{ path: string[] }> },
  method: 'GET' | 'PUT' | 'POST',
) {
  if (method !== 'GET') {
    const rejected = requireSameOriginMutation(request)
    if (rejected) return rejected
  }
  const path = resolvePath((await context.params).path, request, method)
  if (!path) {
    return NextResponse.json({ message: 'Unknown operator action.' }, { status: 404, headers: NO_STORE_HEADERS })
  }
  const token = (await cookies()).get(OPERATOR_COOKIE_NAME)?.value
  if (!token) {
    return NextResponse.json({ message: 'Operator sign-in is required.' }, { status: 401, headers: NO_STORE_HEADERS })
  }
  const rawBody = method === 'GET' ? undefined : await readSmallJson(request)
  const body = rawBody ? sanitizeMutationBody(path.split('?')[0], rawBody) : undefined
  if (method !== 'GET' && !body) {
    return NextResponse.json({ message: 'The request body was invalid.' }, { status: 400, headers: NO_STORE_HEADERS })
  }
  try {
    return sanitizedOperatorResponse(await operatorUpstreamFetch(path, token, { method, body: body ?? undefined }))
  } catch {
    return NextResponse.json({ message: 'The node operator service is unavailable.' }, { status: 503, headers: NO_STORE_HEADERS })
  }
}

export async function GET(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  return proxy(request, context, 'GET')
}

export async function PUT(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  return proxy(request, context, 'PUT')
}

export async function POST(request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  return proxy(request, context, 'POST')
}
