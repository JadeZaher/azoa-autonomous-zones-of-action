'use client'

/**
 * Backward-compatibility shim.
 * Re-exports from the unified AzoaContext so existing imports
 * continue to work while new code can use `useAzoa()` directly.
 */

import { useContext, useMemo, type ReactNode } from 'react'
import { AzoaProvider, useAzoa, AzoaContext } from './azoa-context'

// Re-export the provider under its old name
export { AzoaProvider as AzoaAuthProvider } from './azoa-context'

/**
 * Legacy hook shape — mirrors the old AzoaAuthContextType so
 * every existing consumer keeps working without modification.
 */
interface LegacyAzoaAuthContextType {
  user: ReturnType<typeof useAzoa>['user']
  isAuthenticated: boolean
  loading: boolean
  avatarId: string | null
  login: (email: string, password: string) => Promise<{ success: boolean; error?: string }>
  register: (params: { username: string; email: string; password: string }) => Promise<{ success: boolean; error?: string }>
  logout: () => Promise<void>
}

export function useAzoaAuth(): LegacyAzoaAuthContextType {
  const ctx = useContext(AzoaContext)
  if (!ctx) throw new Error('useAzoaAuth must be used within AzoaAuthProvider')

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
export { useAzoa } from './azoa-context'
export type { AzoaState, WalletEntry } from './azoa-context'
