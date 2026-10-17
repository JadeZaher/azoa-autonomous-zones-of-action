import { NextRequest, NextResponse } from 'next/server'
import { cookies } from 'next/headers'
import {
  OPERATOR_COOKIE_NAME,
  operatorUpstreamFetch,
  readSmallJson,
  requireSameOriginMutation,
  sanitizedOperatorResponse,
} from '@/lib/operator-bff'
import { KYC_PROVIDER_DISPLAY_NAME_MAX_LENGTH } from '@/lib/operator-contracts'

export const dynamic = 'force-dynamic'

const NO_STORE_HEADERS = { 'Cache-Control': 'no-store' }

const GET_PATHS = new Set(['overview', 'kyc/providers'])
const GENESIS_READ_PATH = /^platform-token\/creation\/[0-9a-f]{32}$/i
const GENESIS_MUTATION_PATH = /^platform-token\/creation\/[0-9a-f]{32}\/(quote|accept-quote|continue|replace|refresh)$/i
const PROVIDER_PATH = /^kyc\/providers\/[a-z0-9][a-z0-9_-]{0,63}$/
const WEBHOOK_PATH = /^tenants\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/webhook-registration$/i
const WEBHOOK_WITHDRAW_PATH = /^tenants\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/webhook-registration\/withdraw$/i
const TENANT_PROVIDER_PATH = /^tenants\/[0-9a-f-]{36}\/kyc-provider$/i
const DECISION_PATH = /^kyc\/submissions\/[0-9a-f-]{36}\/decision$/i
const AML_REVIEW_DECISION_PATH = /^kyc\/aml-reviews\/[0-9a-f-]{36}\/decision$/i
const SESSION_REVOKE_PATH = /^session\/revoke$/
const LOT_RECOVERY_PATH = /^on-ramp-lots\/intents\/(?:[0-9a-f]{32}|[0-9a-f-]{36})\/(?:resume|refund)$/i
const EVENT_REPLAY_PATH = /^fiat-events\/[0-9a-f]{64}\/replay$/i
const HOSTED_RECOVERY_PATH = /^hosted-sessions\/(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/(reconcile|refresh)$/i
const ACTION_RECOVERY_READ_PATH = /^action-recovery\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?:\/history)?$/i
const ACTION_RECOVERY_RETRY_PATH = /^action-recovery\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\/retry$/i
const TENANT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const EMPTY_TENANT_ID = '00000000-0000-0000-0000-000000000000'
const WEBHOOK_EVENT_PATH = new RegExp(`^tenants/${TENANT_ID.source.slice(1, -1)}/(?:consent-)?webhook-events(?:/(${TENANT_ID.source.slice(1, -1)})(/retry)?)?$`, 'i')
const PROVIDER_KEY = /^[a-z0-9][a-z0-9_-]{0,63}$/
const AUDIT_ACTIONS = new Set([
  'profile.trust-change',
  'profile.metadata-change',
  'tenant.provider-selection',
])
const AUDIT_QUERY_KEYS = new Set(['limit', 'cursor', 'tenantId', 'providerKey', 'action'])

// Money board reads accept only a bounded limit.
const LIMIT_ONLY_PATHS = new Set(['on-ramp-lots', 'fiat-events', 'hosted-sessions/reconciliation', 'action-recovery'])

function withLimit(path: string, request: NextRequest): string | null {
  const rawLimit = request.nextUrl.searchParams.get('limit') ?? '50'
  if (!/^\d{1,3}$/.test(rawLimit)) return null
  const limit = Number(rawLimit)
  if (limit < 1 || limit > 100) return null
  return `${path}?limit=${limit}`
}

function resolvePath(parts: string[], request: NextRequest, method: string): string | null {
  const path = parts.join('/')
  const webhookEvent = WEBHOOK_EVENT_PATH.exec(path)
  if (webhookEvent) {
    if (webhookEvent[1]) return request.nextUrl.searchParams.size === 0
      && ((method === 'GET' && !webhookEvent[2]) || (method === 'POST' && webhookEvent[2])) ? path : null
    if (method !== 'GET') return null
    const params = request.nextUrl.searchParams
    if ([...params.keys()].some(key => !['offset', 'limit'].includes(key) || params.getAll(key).length !== 1)) return null
    const offset = params.get('offset') ?? '0'
    const limit = params.get('limit') ?? '25'
    if (!/^\d{1,10}$/.test(offset) || Number(offset) > 2147483647 || !/^\d{1,3}$/.test(limit)
      || Number(limit) < 1 || Number(limit) > 100) return null
    return `${path}?offset=${Number(offset)}&limit=${Number(limit)}`
  }
  if (request.nextUrl.searchParams.size === 0 && ((method === 'GET' && (path === 'platform-token' || GENESIS_READ_PATH.test(path)))
    || (method === 'POST' && (path === 'platform-token/genesis' || GENESIS_MUTATION_PATH.test(path))))) return path
  if (method === 'GET' && GET_PATHS.has(path)) return path
  if ((method === 'GET' || method === 'PUT') && WEBHOOK_PATH.test(path)) return path
  if (method === 'POST' && WEBHOOK_WITHDRAW_PATH.test(path)) return path
  if (method === 'GET' && ACTION_RECOVERY_READ_PATH.test(path)) return path
  if (method === 'GET' && LIMIT_ONLY_PATHS.has(path)) return withLimit(path, request)
  if (method === 'GET' && path === 'tenants') {
    return withPaging(path, request, true)
  }
  if (method === 'GET' && path === 'kyc/submissions') {
    const status = request.nextUrl.searchParams.get('status') ?? 'pending'
    if (!['pending', 'approved', 'rejected'].includes(status)) return null
    return withPaging(`${path}?status=${encodeURIComponent(status)}`, request, false)
  }
  if (method === 'GET' && path === 'kyc/aml-reviews') {
    return withPaging(path, request, false)
  }
  if (method === 'GET' && path === 'kyc/audit') {
    return withAuditFilters(path, request)
  }
  if (method === 'PUT' && (PROVIDER_PATH.test(path) || TENANT_PROVIDER_PATH.test(path))) return path
  if (
    method === 'POST' &&
    (DECISION_PATH.test(path) || AML_REVIEW_DECISION_PATH.test(path) || SESSION_REVOKE_PATH.test(path)
      || LOT_RECOVERY_PATH.test(path) || EVENT_REPLAY_PATH.test(path) || HOSTED_RECOVERY_PATH.test(path) || ACTION_RECOVERY_RETRY_PATH.test(path))
  ) {
    return path
  }
  return null
}

function withAuditFilters(path: string, request: NextRequest): string | null {
  const params = request.nextUrl.searchParams
  let validKeys = true
  params.forEach((_value, key) => {
    if (!AUDIT_QUERY_KEYS.has(key) || params.getAll(key).length !== 1) validKeys = false
  })
  if (!validKeys) return null

  const rawLimit = params.get('limit') ?? '25'
  if (!/^\d{1,3}$/.test(rawLimit)) return null
  const limit = Number(rawLimit)
  if (limit < 1 || limit > 100) return null

  const cursor = params.get('cursor')
  if (cursor !== null && (cursor.length < 1 || cursor.length > 512 || !/^[A-Za-z0-9_-]+$/.test(cursor))) {
    return null
  }

  const tenantId = params.get('tenantId')
  if (tenantId !== null && (!TENANT_ID.test(tenantId) || tenantId.toLowerCase() === EMPTY_TENANT_ID)) return null

  const providerKey = params.get('providerKey')
  if (providerKey !== null && !PROVIDER_KEY.test(providerKey)) return null

  const action = params.get('action')
  if (action !== null && !AUDIT_ACTIONS.has(action)) return null

  const query = new URLSearchParams({ limit: String(limit) })
  if (cursor) query.set('cursor', cursor)
  if (tenantId) query.set('tenantId', tenantId)
  if (providerKey) query.set('providerKey', providerKey)
  if (action) query.set('action', action)
  return `${path}?${query.toString()}`
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
  if (WEBHOOK_EVENT_PATH.test(path) && path.endsWith('/retry')) {
    const requestId = stringField(body, 'requestId', 36)
    const expectedVersion = numberField(body, 'expectedVersion')
    const reason = stringField(body, 'reason', 1000)
    return requestId && TENANT_ID.test(requestId) && requestId !== EMPTY_TENANT_ID
      && expectedVersion !== undefined && reason && reason.length >= 10
      ? { requestId, expectedVersion, reason } : null
  }
  if (path === 'platform-token/genesis') {
    const chainType = stringField(body, 'chainType', 32)
    const name = stringField(body, 'name', 128)
    const unitName = stringField(body, 'unitName', 32)
    const expectedNetwork = stringField(body, 'expectedNetwork', 16)
    const total = stringField(body, 'total', 20)
    const decimals = numberField(body, 'decimals')
    if (!chainType || !name || !unitName || !expectedNetwork || !['Devnet', 'Testnet', 'Mainnet'].includes(expectedNetwork)
      || !total || !/^[1-9][0-9]*$/.test(total) || BigInt(total) > 18446744073709551615n
      || decimals === undefined || decimals > 19 || body.confirmIrreversible !== true) return null
    return { chainType, name, unitName, expectedNetwork, total, decimals, confirmIrreversible: true }
  }
  if (GENESIS_MUTATION_PATH.test(path)) {
    if (path.endsWith('/quote') || path.endsWith('/refresh')) return Object.keys(body).length === 0 ? {} : null
    const version = numberField(body, 'version')
    if (version === undefined) return null
    if (path.endsWith('/accept-quote')) {
      const quoteId = stringField(body, 'quoteId', 64)
      return quoteId && /^[0-9a-f]{64}$/.test(quoteId) ? { version, quoteId } : null
    }
    const requestId = stringField(body, 'requestId', 36)
    return requestId && TENANT_ID.test(requestId) && requestId !== EMPTY_TENANT_ID ? { version, requestId } : null
  }
  if (WEBHOOK_PATH.test(path) || WEBHOOK_WITHDRAW_PATH.test(path)) {
    const requestId = stringField(body, 'requestId', 36)
    const registrationId = stringField(body, 'registrationId', 36)
    const expectedVersion = numberField(body, 'expectedVersion')
    const reason = stringField(body, 'reason', 1000)
    if (!requestId || !TENANT_ID.test(requestId) || requestId === EMPTY_TENANT_ID
      || !registrationId || !TENANT_ID.test(registrationId) || registrationId === EMPTY_TENANT_ID
      || expectedVersion === undefined || !reason || reason.length < 10
      || typeof body.url !== 'string' || body.url.length > 2048
      || typeof body.isActive !== 'boolean' || typeof body.rotateSecret !== 'boolean'
      || (body.replaceLegacy !== undefined && typeof body.replaceLegacy !== 'boolean')
      || (body.legacyFingerprint != null && (typeof body.legacyFingerprint !== 'string' || !/^[0-9a-f]{64}$/.test(body.legacyFingerprint)))) return null
    return { requestId, registrationId, expectedVersion, url: body.url, isActive: body.isActive,
      rotateSecret: body.rotateSecret, reason, replaceLegacy: body.replaceLegacy ?? false, legacyFingerprint: body.legacyFingerprint ?? null }
  }
  if (PROVIDER_PATH.test(path)) {
    const displayName = stringField(body, 'displayName', KYC_PROVIDER_DISPLAY_NAME_MAX_LENGTH)
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
  if (AML_REVIEW_DECISION_PATH.test(path)) {
    // Deliberately distinct from the submission queue above: the verbs are clear/deny and
    // the free-text field is `reason`, not `notes`. A reason is required either way, because
    // both outcomes are a human decision someone may later have to answer for.
    const decision = body.decision
    const reason = stringField(body, 'reason', 2_000)
    const expectedScreeningId = stringField(body, 'expectedScreeningId', 128)
    if (decision !== 'clear' && decision !== 'deny') return null
    if (!reason || !expectedScreeningId) return null
    const tenantId = stringField(body, 'tenantId', 36)
    if (tenantId !== undefined && !TENANT_ID.test(tenantId)) return null
    return tenantId ? { decision, reason, expectedScreeningId, tenantId } : { decision, reason, expectedScreeningId }
  }
  if (SESSION_REVOKE_PATH.test(path)) {
    return Object.keys(body).length === 0 ? {} : null
  }
  if (EVENT_REPLAY_PATH.test(path)) return Object.keys(body).length === 0 ? {} : null
  if (ACTION_RECOVERY_RETRY_PATH.test(path)) {
    const requestId = stringField(body, 'requestId', 36)
    const intentHash = stringField(body, 'intentHash', 64)
    const reason = stringField(body, 'reason', 1000)
    const expectedVersion = body.expectedVersion
    if (!requestId || !TENANT_ID.test(requestId) || !intentHash || !/^[0-9a-f]{64}$/i.test(intentHash)
      || !reason || reason.trim().length < 20 || typeof expectedVersion !== 'number'
      || !Number.isSafeInteger(expectedVersion) || expectedVersion < 0) return null
    return { requestId, intentHash, reason: reason.trim(), expectedVersion }
  }
  if (HOSTED_RECOVERY_PATH.test(path)) {
    if (path.endsWith('/refresh')) return Object.keys(body).length === 0 ? {} : null
    const reason = stringField(body, 'reason', 1000)
    const providerSessionId = stringField(body, 'providerSessionId', 256)
    const redirectUrl = stringField(body, 'redirectUrl', 2048)
    if (!reason || reason.length < 20 || body.operatorReviewedSubjectAndProvider !== true
      || typeof body.confirmedProviderAbsence !== 'boolean'
      || body.confirmedProviderAbsence === Boolean(providerSessionId)) return null
    return { reason, providerSessionId, redirectUrl, confirmedProviderAbsence: body.confirmedProviderAbsence,
      operatorReviewedSubjectAndProvider: true }
  }
  if (LOT_RECOVERY_PATH.test(path)) {
    const reason = stringField(body, 'reason', 512)
    const providerRefundId = stringField(body, 'providerRefundId', 128)
    if (!reason || (providerRefundId !== undefined && !/^re_[A-Za-z0-9]+$/.test(providerRefundId))) return null
    if (body.retryFailed !== undefined && typeof body.retryFailed !== 'boolean') return null
    const expectedRefundAttempt = numberField(body, 'expectedRefundAttempt')
    if (body.retryFailed === true && expectedRefundAttempt === undefined) return null
    return { reason, retryFailed: body.retryFailed === true, expectedRefundAttempt, ...(providerRefundId ? { providerRefundId } : {}) }
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
  const requestedParts = (await context.params).path
  const requestedPath = requestedParts.join('/')
  const path = resolvePath(requestedParts, request, method)
  if (!path) {
    const invalidAuditQuery = method === 'GET' && requestedPath === 'kyc/audit'
    return NextResponse.json(
      { message: invalidAuditQuery ? 'The audit filters were invalid.' : 'Unknown operator action.' },
      { status: invalidAuditQuery ? 400 : 404, headers: NO_STORE_HEADERS },
    )
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
