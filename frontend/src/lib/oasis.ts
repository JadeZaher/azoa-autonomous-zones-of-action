'use client'

/**
 * OASIS SDK integration for the frontend.
 *
 * Replaces the hand-rolled BlockchainApiClient and mock auth with
 * the @oasis/sdk OasisClient, providing:
 * - Real API calls to the .NET backend
 * - Session management with localStorage
 * - Multi-chain wallet with client-side signing
 * - Holon querying via fluent API
 * - Portfolio aggregation
 */

import {
  OasisClient,
  isOk,
  isErr,
} from '@oasis/sdk'
import { buildChainRegistrations, readInitialNetwork } from './networks'
import { readInitialDebug } from './debug'
import type {
  SessionState,
  HolonResult,
  BalanceInfo,
  ChainBalance,
  PortfolioSummary,
  AuthProfile,
  Result,
  SdkError,
} from '@oasis/sdk'

/** Placeholder type for avatar API responses (not exported by SDK) */
export type AvatarResponse = {
  id: string
  username: string
  email: string
  [key: string]: unknown
}

// ─── localStorage session adapter ───

const localStorageAdapter = {
  get: async (key: string) => {
    if (typeof window === 'undefined') return null
    return localStorage.getItem(key)
  },
  set: async (key: string, value: string) => {
    if (typeof window !== 'undefined') localStorage.setItem(key, value)
  },
  remove: async (key: string) => {
    if (typeof window !== 'undefined') localStorage.removeItem(key)
  },
}

// ─── SDK singleton ───

const API_BASE_URL = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:5000'

// The initial network is read (SSR-safe) from the persisted choice so the very
// first wallet operation already targets the right network. Runtime switching
// is handled by oasis.setNetwork() (see network-context.tsx) — the SDK rebuilds
// every chain provider for the new network, so operations can never span
// devnet/testnet/mainnet.
const initialNetwork = readInitialNetwork()

// Verbose SDK diagnostics: seeded from the persisted user choice (falling back
// to "on" in any non-production build). Pairs with the backend's
// OASIS:DebugErrors so the server's exception chain flows all the way into
// SdkError.detail. Runtime toggling is handled by oasis.setDebug() — see
// debug-context.tsx / the top-nav DebugSwitcher.
const DEBUG = readInitialDebug()

export const oasis = new OasisClient({
  apiUrl: API_BASE_URL,
  sessionStorage: localStorageAdapter,
  network: initialNetwork,
  chains: buildChainRegistrations(initialNetwork),
  chainsForNetwork: buildChainRegistrations,
  debug: DEBUG,
})

// ─── Helper wrappers for components ───

/** Unwrap a Result<T> for use in React components. Throws on error for error boundaries. */
export function unwrapResult<T>(result: Result<T, SdkError>): T {
  if (isOk(result)) return result.value
  throw new Error(result.error.message)
}

/** Safe unwrap — returns null on error instead of throwing. */
export function safeUnwrap<T>(result: Result<T, SdkError>): T | null {
  return isOk(result) ? result.value : null
}

// Re-export types for component use
export type {
  SessionState,
  HolonResult,
  BalanceInfo,
  ChainBalance,
  PortfolioSummary,
  AuthProfile,
}
export { isOk, isErr }
