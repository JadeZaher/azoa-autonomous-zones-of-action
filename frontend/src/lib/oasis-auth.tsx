'use client'

/**
 * Backward-compatibility shim.
 * Re-exports from the unified OasisContext so existing imports
 * continue to work while new code can use `useOasis()` directly.
 */

import { useContext, useMemo, type ReactNode } from 'react'
import { OasisProvider, useOasis, OasisContext } from './oasis-context'

// Re-export the provider under its old name
export { OasisProvider as OasisAuthProvider } from './oasis-context'

/**
 * Legacy hook shape — mirrors the old OasisAuthContextType so
 * every existing consumer keeps working without modification.
 */
interface LegacyOasisAuthContextType {
  user: ReturnType<typeof useOasis>['user']
  isAuthenticated: boolean
  loading: boolean
  avatarId: string | null
  login: (email: string, password: string) => Promise<{ success: boolean; error?: string }>
  register: (params: { username: string; email: string; password: string }) => Promise<{ success: boolean; error?: string }>
  logout: () => Promise<void>
}

export function useOasisAuth(): LegacyOasisAuthContextType {
  const ctx = useContext(OasisContext)
  if (!ctx) throw new Error('useOasisAuth must be used within OasisAuthProvider')

  return useMemo(
    () => ({
      user: ctx.user,
      isAuthenticated: ctx.isAuthenticated,
      loading: ctx.authLoading,
      avatarId: ctx.avatarId,
      login: ctx.login,
      register: ctx.register,
      logout: ctx.logout,
    }),
    [ctx]
  )
}

// Re-export for new code
export { useOasis } from './oasis-context'
export type { OasisState, WalletEntry } from './oasis-context'
