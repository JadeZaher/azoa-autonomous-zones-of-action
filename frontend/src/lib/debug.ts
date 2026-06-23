'use client'

/**
 * Shared debug-mode registry.
 *
 * "Debug mode" turns on verbose SDK diagnostics: every request/response/error
 * is logged to the browser console, and the backend's server-side exception
 * chain (when the API runs with `AZOA:DebugErrors`) is surfaced in
 * `SdkError.detail` — which the Functional Test Runner renders in its
 * LLM-friendly error dump via `SdkError.debugString()`.
 *
 * It is a GLOBAL, app-wide switch persisted to localStorage and synced into
 * the SDK client at runtime (see debug-context.tsx). The default is ON in any
 * non-production build (and whenever `NEXT_PUBLIC_AZOA_DEBUG === 'true'`) so
 * the test page has rich errors out of the box; the user can override via the
 * top-nav switcher and the choice sticks across reloads.
 *
 * This module is intentionally free of React/SDK imports so both the SDK
 * singleton (azoa.ts) and the React context can read the initial value
 * without an import cycle — same split as networks.ts / network-context.tsx.
 */

export const DEBUG_STORAGE_KEY = 'azoa.debug'

/** Default when the user has not made an explicit persisted choice. */
export function defaultDebug(): boolean {
  return (
    process.env.NEXT_PUBLIC_AZOA_DEBUG === 'true' ||
    process.env.NODE_ENV !== 'production'
  )
}

/** SSR-safe read of the persisted choice, falling back to {@link defaultDebug}. */
export function readInitialDebug(): boolean {
  if (typeof window === 'undefined') return defaultDebug()
  const stored = window.localStorage.getItem(DEBUG_STORAGE_KEY)
  if (stored === 'true') return true
  if (stored === 'false') return false
  return defaultDebug()
}

/** True only if the user has explicitly chosen a debug setting (persisted). */
export function hasPersistedDebug(): boolean {
  if (typeof window === 'undefined') return false
  const stored = window.localStorage.getItem(DEBUG_STORAGE_KEY)
  return stored === 'true' || stored === 'false'
}
