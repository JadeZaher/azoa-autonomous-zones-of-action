// Idempotency-key lifecycle for first-party money requests. See `src/app/(dashboard)/AGENTS.md` §first-party-money.

/** A caller-stable idempotency key saved before the request leaves, so a lost response resumes instead of charging twice. */
export interface SavedRequest {
  requestKey: string
  amountMinorUnits: number
  /** The server's id for this key (e.g. a checkout intent id), once an answer named it. */
  serverId?: string
}

/** `ready`: send with this key. `pending`: an unresolved key for a different amount must be resolved first. */
export type RequestClaim = { kind: 'ready'; request: SavedRequest } | { kind: 'pending'; pending: SavedRequest }

const storageKey = (scope: string, userId: string) => `azoa:${scope}:request:${userId}`

/** Reuses the saved key for the same amount; never replaces an unresolved key for a different amount. */
export function claimRequestKey(scope: string, userId: string, amountMinorUnits: number): RequestClaim {
  const saved = readSavedRequest(scope, userId)
  if (saved) return saved.amountMinorUnits === amountMinorUnits ? { kind: 'ready', request: saved } : { kind: 'pending', pending: saved }
  const fresh = { requestKey: crypto.randomUUID(), amountMinorUnits }
  localStorage.setItem(storageKey(scope, userId), JSON.stringify(fresh))
  return { kind: 'ready', request: fresh }
}

/** Records the server id an answer gave for the saved key, so a later visit can resolve it by state. */
export function rememberServerId(scope: string, userId: string, requestKey: string, serverId: string): void {
  const saved = readSavedRequest(scope, userId)
  if (saved?.requestKey === requestKey) localStorage.setItem(storageKey(scope, userId), JSON.stringify({ ...saved, serverId }))
}

export function readSavedRequest(scope: string, userId: string): SavedRequest | null {
  try {
    const raw = localStorage.getItem(storageKey(scope, userId))
    const parsed = raw ? (JSON.parse(raw) as Partial<SavedRequest>) : null
    return parsed && typeof parsed.requestKey === 'string' && Number.isSafeInteger(parsed.amountMinorUnits)
      ? {
          requestKey: parsed.requestKey,
          amountMinorUnits: parsed.amountMinorUnits as number,
          ...(typeof parsed.serverId === 'string' ? { serverId: parsed.serverId } : {}),
        }
      : null
  } catch {
    return null
  }
}

/** Forget the key once the server has given a definite answer for it (terminal state or typed refusal). */
export function clearSavedRequest(scope: string, userId: string): void {
  localStorage.removeItem(storageKey(scope, userId))
}

/** A checkout intent the key can no longer usefully resume: paid, failed or expired. */
export function isFinishedCheckout(intent: { status: string; settlementState: string }): boolean {
  return ['complete', 'failed', 'expired'].includes(intent.status)
    || intent.settlementState === 'funds_applied' || intent.settlementState === 'payment_failed'
}

/**
 * A refusal that is a definite answer for the key: 400/403/404/422. Network failures, 401, 409
 * (in progress), 429 and 5xx keep the key, because the request may still have been admitted.
 */
export function isDefiniteRefusal(status: number | undefined): boolean {
  return status === 400 || status === 403 || status === 404 || status === 422
}

/** Parses a decimal amount in major units (e.g. "12.50") into integer minor units; null when not exact. */
export function toMinorUnits(text: string): number | null {
  const match = /^(\d{1,9})(?:\.(\d{1,2}))?$/.exec(text.trim())
  if (!match) return null
  return Number(match[1]) * 100 + Number((match[2] ?? '').padEnd(2, '0'))
}

/** Formats integer minor units for display only. */
export function formatMinorUnits(minorUnits: number, currencyCode: string): string {
  const sign = minorUnits < 0 ? '-' : ''
  const absolute = Math.abs(minorUnits)
  return `${sign}${Math.floor(absolute / 100).toLocaleString()}.${String(absolute % 100).padStart(2, '0')} ${currencyCode.toUpperCase()}`.trim()
}
