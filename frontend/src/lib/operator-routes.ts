// Round-4 operator console allowlist: one narrow entry per page action. See `src/app/operator/AGENTS.md` §round-4-console.

type Method = 'GET' | 'PUT' | 'POST'
type Body = Record<string, unknown>

/** One allowlisted console action: console path → exact upstream API path and a rebuilt body. */
export interface ConsoleRoute {
  method: Method
  /** Anchored pattern over the console path (after `/api/operator/`). */
  pattern: RegExp
  /** Upstream `/api/...` path for a matched console path, or null when a capture is invalid. */
  upstream: (path: string, match: RegExpExecArray) => string
  /** Query normalizer; absent = no query allowed. Returns the canonical query string or null when invalid. */
  query?: (params: URLSearchParams) => string | null
  /** Body rebuilder for mutations; returns null on any invalid field. */
  body?: (body: Body) => Body | null
  /** Upstream answers a bare JSON body instead of an `AZOAResult` envelope. */
  bare?: boolean
}

export interface ResolvedConsoleRoute {
  upstreamPath: string
  body?: Body
  bare: boolean
}

const UUID = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
const HEX32 = '[0-9a-f]{32}'
const HEX64 = '[0-9a-f]{64}'
const NETWORK = '(Devnet|Testnet|Mainnet)'
/** `FiatPayout.RecordIdFor` (hex32) and `FiatReversal.RecordIdFor` (hex64). */
const PAYOUT_ID = HEX32
const REVERSAL_ID = HEX64

function text(body: Body, key: string, min: number, max: number): string | undefined {
  const value = body[key]
  if (typeof value !== 'string') return undefined
  const trimmed = value.trim()
  return trimmed.length >= min && trimmed.length <= max ? trimmed : undefined
}

function version(body: Body, key: string): number | undefined {
  const value = body[key]
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : undefined
}

function onlyKeys(body: Body, allowed: string[]): boolean {
  return Object.keys(body).every(key => allowed.includes(key))
}

const emptyBody = (body: Body) => (Object.keys(body).length === 0 ? {} : null)

/** `?limit=` only, bounded to [1, max]; default when absent. */
function limitQuery(max: number, fallback = 50) {
  return (params: URLSearchParams) => {
    if ([...params.keys()].some(key => key !== 'limit') || params.getAll('limit').length > 1) return null
    const raw = params.get('limit') ?? String(fallback)
    if (!/^\d{1,3}$/.test(raw)) return null
    const limit = Number(raw)
    return limit >= 1 && limit <= max ? `limit=${limit}` : null
  }
}

/** Saga dead-letter filter: repeated `status` from the three server-known values plus a bounded limit. */
function sagaQuery(params: URLSearchParams): string | null {
  if ([...params.keys()].some(key => key !== 'status' && key !== 'limit')) return null
  const statuses = params.getAll('status')
  if (statuses.some(status => !['DeadLettered', 'Parked', 'Cancelled'].includes(status)) || statuses.length > 3) return null
  const limit = limitQuery(500, 100)(new URLSearchParams(params.has('limit') ? { limit: params.get('limit')! } : {}))
  if (!limit) return null
  return [...statuses.map(status => `status=${status}`), limit].join('&')
}

function reasonBody(min: number) {
  return (body: Body) => {
    const reason = text(body, 'reason', min, 1000)
    return reason && onlyKeys(body, ['reason']) ? { reason } : null
  }
}

function feeEntry(value: unknown): Body | null | undefined {
  if (value === undefined || value === null) return undefined
  if (typeof value !== 'object' || Array.isArray(value)) return null
  const entry = value as Body
  if (!onlyKeys(entry, ['flatBaseUnits', 'bps'])) return null
  const flat = entry.flatBaseUnits
  const bps = entry.bps
  if (flat !== undefined && (typeof flat !== 'string' || !/^\d{1,20}$/.test(flat))) return null
  if (bps !== undefined && (typeof bps !== 'number' || !Number.isSafeInteger(bps) || bps < 0 || bps > 10_000)) return null
  return { ...(flat !== undefined ? { flatBaseUnits: flat } : {}), ...(bps !== undefined ? { bps } : {}) }
}

const FEE_KEYS = ['mint', 'transfer', 'swap', 'actionComplete', 'federationPublish']

export const CONSOLE_ROUTES: ConsoleRoute[] = [
  // D-3 stuck client-signed operations.
  { method: 'GET', pattern: /^blockchain-operations\/stuck$/, upstream: () => '/api/operator/blockchain-operations/stuck', query: limitQuery(200) },
  {
    method: 'POST', pattern: new RegExp(`^blockchain-operations/(${UUID})/resolve$`, 'i'),
    upstream: (_path, match) => `/api/operator/blockchain-operations/${match[1].toLowerCase()}/resolve`,
    body: body => {
      const reason = text(body, 'reason', 10, 1000)
      const transactionId = body.transactionId === undefined ? undefined : text(body, 'transactionId', 1, 128)
      if (!reason || !onlyKeys(body, ['reason', 'transactionId'])) return null
      if (body.transactionId !== undefined && (!transactionId || !/^[A-Za-z0-9_-]+$/.test(transactionId))) return null
      return transactionId ? { reason, transactionId } : { reason }
    },
  },
  // Deployment operator exit (F-H12).
  { method: 'GET', pattern: /^deployments$/, upstream: () => '/api/operator/deployments', query: limitQuery(100) },
  {
    method: 'POST', pattern: new RegExp(`^deployments/(${HEX32})/resolve$`),
    upstream: (_path, match) => `/api/operator/deployments/${match[1]}/resolve`,
    body: body => {
      const expectedVersion = version(body, 'expectedVersion')
      const action = body.action
      const reason = body.reason === undefined ? undefined : text(body, 'reason', 10, 1000)
      if (expectedVersion === undefined || (action !== 'Reobserve' && action !== 'CloseUnresolvable')
        || typeof body.acknowledgeNoFurtherEffect !== 'boolean' || (body.reason !== undefined && !reason)
        || !onlyKeys(body, ['expectedVersion', 'action', 'reason', 'acknowledgeNoFurtherEffect'])) return null
      if (action === 'CloseUnresolvable' && (body.acknowledgeNoFurtherEffect !== true || !reason)) return null
      return { expectedVersion, action, acknowledgeNoFurtherEffect: body.acknowledgeNoFurtherEffect, ...(reason ? { reason } : {}) }
    },
  },
  // Retired exchange rows (Operator policy, outside /api/operator).
  { method: 'GET', pattern: /^exchange\/retired-rows$/, upstream: () => '/api/exchange/operator/retired-rows', query: limitQuery(100) },
  {
    method: 'POST', pattern: new RegExp(`^exchange/retired-rows/(${UUID})/resolve$`, 'i'),
    upstream: (_path, match) => `/api/exchange/operator/retired-rows/${match[1].toLowerCase()}/resolve`,
    body: body => {
      const reason = text(body, 'reason', 10, 1000)
      return reason && (body.outcome === 'Confirmed' || body.outcome === 'Failed') && onlyKeys(body, ['outcome', 'reason'])
        ? { outcome: body.outcome, reason } : null
    },
  },
  // Wallet identity migration conflicts.
  // GET answers a bare `WalletAuthIdentityMigrationService.View`; the clear answers `AZOAResult<bool>`.
  { method: 'GET', pattern: /^wallet-identities$/, upstream: () => '/api/operator/wallet-identities', bare: true },
  {
    method: 'POST', pattern: /^wallet-identities\/clear-conflicting-binding$/, upstream: () => '/api/operator/wallet-identities/clear-conflicting-binding',
    body: body => {
      const v = version(body, 'version')
      const userId = text(body, 'userId', 1, 64)
      const expectedIdentity = text(body, 'expectedIdentity', 1, 256)
      const reason = text(body, 'reason', 10, 1000)
      return v !== undefined && userId && expectedIdentity && reason && onlyKeys(body, ['version', 'userId', 'expectedIdentity', 'reason'])
        ? { version: v, userId, expectedIdentity, reason } : null
    },
  },
  // DEX settle-review.
  {
    method: 'POST', pattern: new RegExp(`^dex/operations/(${HEX64})/settle-review$`),
    upstream: (_path, match) => `/api/operator/dex/operations/${match[1]}/settle-review`,
    body: body => {
      const expectedVersion = version(body, 'expectedVersion')
      const reason = text(body, 'reason', 10, 1000)
      return expectedVersion !== undefined && reason && onlyKeys(body, ['expectedVersion', 'reason']) ? { expectedVersion, reason } : null
    },
  },
  // WTT inspection, recovery actions, stage-key keyring.
  { method: 'GET', pattern: new RegExp(`^wtt/operations/(${HEX64})$`), upstream: (_path, match) => `/api/operator/wtt/operations/${match[1]}` },
  {
    method: 'POST', pattern: new RegExp(`^wtt/operations/(${HEX64})/(reconcile|refetch-proof|resume-proof)$`),
    upstream: (_path, match) => `/api/operator/wtt/operations/${match[1]}/${match[2]}`, body: emptyBody,
  },
  { method: 'GET', pattern: /^wtt\/stage-keys\/readiness$/, upstream: () => '/api/operator/wtt/stage-keys/readiness' },
  { method: 'POST', pattern: /^wtt\/stage-keys\/rewrap$/, upstream: () => '/api/operator/wtt/stage-keys/rewrap', body: emptyBody },
  // Ethereum prepared-transaction recovery (platform signer).
  // GET answers a bare `IReadOnlyList<EthereumPreparationView>`; activate/recover answer `AZOAResult`.
  { method: 'GET', pattern: new RegExp(`^ethereum/recovery/${NETWORK}$`), upstream: (_path, match) => `/api/operator/ethereum/recovery/${match[1]}`, bare: true },
  {
    method: 'POST', pattern: new RegExp(`^ethereum/recovery/${NETWORK}/activate$`),
    upstream: (_path, match) => `/api/operator/ethereum/recovery/${match[1]}/activate`,
    body: body => {
      const sender = text(body, 'sender', 42, 42)
      return sender && /^0x[0-9a-fA-F]{40}$/.test(sender) && onlyKeys(body, ['sender']) ? { sender } : null
    },
  },
  {
    method: 'POST', pattern: new RegExp(`^ethereum/recovery/${NETWORK}/([A-Za-z0-9_-]{1,128})$`),
    upstream: (_path, match) => `/api/operator/ethereum/recovery/${match[1]}/${match[2]}`,
    body: body => {
      const reason = text(body, 'reason', 10, 1000)
      const fee = text(body, 'maximumFeePerGas', 1, 30)
      return reason && fee && /^\d+$/.test(fee) && typeof body.cancel === 'boolean' && onlyKeys(body, ['cancel', 'maximumFeePerGas', 'reason'])
        ? { cancel: body.cancel, maximumFeePerGas: fee, reason } : null
    },
  },
  // Generic saga dead letters (action-workflow steps are refused upstream and go through action recovery).
  { method: 'GET', pattern: /^sagas\/dead-letters$/, upstream: () => '/api/admin/sagas/dead-letters', query: sagaQuery, bare: true },
  { method: 'POST', pattern: new RegExp(`^sagas/(${UUID})/requeue$`, 'i'), upstream: (_path, match) => `/api/admin/sagas/${match[1].toLowerCase()}/requeue`, body: emptyBody, bare: true },
  {
    method: 'POST', pattern: new RegExp(`^sagas/(${UUID})/cancel$`, 'i'),
    upstream: (_path, match) => `/api/admin/sagas/${match[1].toLowerCase()}/cancel`, body: reasonBody(10), bare: true,
  },
  // Node fee control (NodeGovern scope on the operator token).
  { method: 'GET', pattern: /^node-governance\/fee-schedule$/, upstream: () => '/api/node-governance/fee-schedule' },
  {
    method: 'PUT', pattern: /^node-governance\/fee-schedule$/, upstream: () => '/api/node-governance/fee-schedule',
    body: body => {
      if (!onlyKeys(body, ['expectedVersion', 'swapFlatDenomination', ...FEE_KEYS])) return null
      const expectedVersion = version(body, 'expectedVersion')
      if (expectedVersion === undefined) return null
      const rebuilt: Body = { expectedVersion }
      for (const key of FEE_KEYS) {
        const entry = feeEntry(body[key])
        if (entry === null) return null
        if (entry) rebuilt[key] = entry
      }
      if (body.swapFlatDenomination !== undefined) {
        if (body.swapFlatDenomination !== 'input-base-units-v1') return null
        rebuilt.swapFlatDenomination = body.swapFlatDenomination
      }
      return rebuilt
    },
  },
  { method: 'GET', pattern: /^node-governance\/fee-audit$/, upstream: () => '/api/node-governance/fee-audit', query: limitQuery(100) },
  // Money boards moved from `/admin/*` (QA M1): Operator policy routes the user Bearer can never reach.
  { method: 'GET', pattern: /^admin\/fiat-payouts$/, upstream: () => '/api/admin/fiat-payouts', query: limitQuery(500, 100) },
  {
    method: 'POST', pattern: new RegExp(`^admin/fiat-payouts/(${PAYOUT_ID})/(advance|restart)$`),
    upstream: (_path, match) => `/api/admin/fiat-payouts/${match[1]}/${match[2]}`, body: emptyBody,
  },
  {
    method: 'POST', pattern: new RegExp(`^admin/fiat-payouts/(${PAYOUT_ID})/(return|park|recover)$`),
    upstream: (_path, match) => `/api/admin/fiat-payouts/${match[1]}/${match[2]}`, body: reasonBody(1),
  },
  {
    method: 'POST', pattern: new RegExp(`^admin/fiat-payouts/(${PAYOUT_ID})/resolve$`),
    upstream: (_path, match) => `/api/admin/fiat-payouts/${match[1]}/resolve`,
    body: body => {
      const reason = text(body, 'reason', 1, 512)
      return reason && (body.resolution === 'record-paid' || body.resolution === 'return-units') && onlyKeys(body, ['resolution', 'reason'])
        ? { resolution: body.resolution, reason } : null
    },
  },
  {
    method: 'POST', pattern: new RegExp(`^admin/fiat-payouts/accounts/(${UUID})/setup/resolve$`, 'i'),
    upstream: (_path, match) => `/api/admin/fiat-payouts/accounts/${match[1].toLowerCase()}/setup/resolve`, body: reasonBody(1),
  },
  { method: 'GET', pattern: /^admin\/fiat-reversals$/, upstream: () => '/api/admin/fiat-reversals', query: limitQuery(500, 100) },
  {
    method: 'POST', pattern: new RegExp(`^admin/fiat-reversals/(${REVERSAL_ID})/(recover|settle|restart)$`),
    upstream: (_path, match) => `/api/admin/fiat-reversals/${match[1]}/${match[2]}`, body: emptyBody,
  },
  {
    method: 'POST', pattern: new RegExp(`^admin/fiat-reversals/(${REVERSAL_ID})/(resolve|clear-flag)$`),
    upstream: (_path, match) => `/api/admin/fiat-reversals/${match[1]}/${match[2]}`, body: reasonBody(1),
  },
  // Parked-run re-probe (Operator policy on ActionController); observes settlement truth, never re-sends.
  { method: 'POST', pattern: /^action\/runs\/reconcile-sweep$/, upstream: () => '/api/action/runs/reconcile-sweep', body: emptyBody },
]

/** Resolves a console request against the round-4 allowlist; null when no entry admits it exactly. */
export function resolveConsoleRoute(path: string, method: Method, params: URLSearchParams, body: Body | null): ResolvedConsoleRoute | 'invalid-body' | null {
  for (const route of CONSOLE_ROUTES) {
    if (route.method !== method) continue
    const match = route.pattern.exec(path)
    if (!match) continue
    let query = ''
    if (route.query) {
      const normalized = route.query(params)
      if (normalized === null) return null
      query = normalized ? `?${normalized}` : ''
    } else if ([...params.keys()].length > 0) {
      return null
    }
    const upstreamPath = route.upstream(path, match) + query
    if (method === 'GET') return { upstreamPath, bare: route.bare === true }
    const rebuilt = body && route.body ? route.body(body) : null
    if (!rebuilt) return 'invalid-body'
    return { upstreamPath, body: rebuilt, bare: route.bare === true }
  }
  return null
}
