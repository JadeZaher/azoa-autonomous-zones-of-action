'use client'

/**
 * DebugProvider — global, app-wide verbose-diagnostics state.
 *
 * Source of truth is the SDK client (`azoa.debug` / `azoa.setDebug`). The
 * provider seeds from the persisted choice, keeps the SDK client in sync on
 * every change, and persists the user's selection. Flipping it has an
 * immediate runtime effect — no page reload, no client rebuild — so the
 * Functional Test Runner's error dump gains/loses the backend exception chain
 * the next time tests run.
 *
 * Same split as networks.ts / network-context.tsx: the pure read/storage
 * helpers live in debug.ts so azoa.ts can seed without an import cycle.
 */

import {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  useMemo,
  type ReactNode,
} from 'react'
import { azoa } from './azoa'
import { DEBUG_STORAGE_KEY, readInitialDebug } from './debug'

interface DebugContextValue {
  /** Whether verbose SDK diagnostics are currently on. */
  debug: boolean
  /** Toggle verbose diagnostics (syncs the SDK + persists, no reload). */
  setDebug: (enabled: boolean) => void
}

const DebugContext = createContext<DebugContextValue | undefined>(undefined)
export { DebugContext }

export function DebugProvider({ children }: { children: ReactNode }) {
  const [debug, setDebugState] = useState<boolean>(() => readInitialDebug())

  // Keep the SDK client authoritative with the chosen mode on mount and on
  // every change (the singleton already seeds from readInitialDebug(), but
  // this also covers any post-construction divergence / fast-refresh).
  useEffect(() => {
    azoa.setDebug(debug)
  }, [debug])

  const setDebug = useCallback((enabled: boolean) => {
    azoa.setDebug(enabled)
    if (typeof window !== 'undefined') {
      window.localStorage.setItem(DEBUG_STORAGE_KEY, String(enabled))
    }
    setDebugState(enabled)
  }, [])

  const value = useMemo<DebugContextValue>(
    () => ({ debug, setDebug }),
    [debug, setDebug],
  )

  return <DebugContext.Provider value={value}>{children}</DebugContext.Provider>
}

export function useDebug(): DebugContextValue {
  const ctx = useContext(DebugContext)
  if (!ctx) throw new Error('useDebug must be used within a DebugProvider')
  return ctx
}
