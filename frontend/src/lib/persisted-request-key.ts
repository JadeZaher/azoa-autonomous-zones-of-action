/** A caller-stable idempotency key saved before the request leaves, so a lost response resumes instead of charging twice. */
export interface SavedRequest {
  requestKey: string
  amountMinorUnits: number
}

const storageKey = (scope: string, userId: string) => `azoa:${scope}:request:${userId}`

/** Reuses the saved key for the same amount; a different amount is a different request. */
export function requestKeyFor(scope: string, userId: string, amountMinorUnits: number): SavedRequest {
  const saved = readSavedRequest(scope, userId)
  if (saved && saved.amountMinorUnits === amountMinorUnits) return saved
  const fresh = { requestKey: crypto.randomUUID(), amountMinorUnits }
  localStorage.setItem(storageKey(scope, userId), JSON.stringify(fresh))
  return fresh
}

export function readSavedRequest(scope: string, userId: string): SavedRequest | null {
  try {
    const raw = localStorage.getItem(storageKey(scope, userId))
    const parsed = raw ? (JSON.parse(raw) as Partial<SavedRequest>) : null
    return parsed && typeof parsed.requestKey === 'string' && Number.isSafeInteger(parsed.amountMinorUnits)
      ? { requestKey: parsed.requestKey, amountMinorUnits: parsed.amountMinorUnits as number }
      : null
  } catch {
    return null
  }
}

/** Forget the key only once the server has given a definite answer for it. */
export function clearSavedRequest(scope: string, userId: string): void {
  localStorage.removeItem(storageKey(scope, userId))
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
