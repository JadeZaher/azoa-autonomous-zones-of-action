'use client'

import React, {
  createContext,
  useContext,
  useState,
  useEffect,
  useCallback,
  useMemo,
  ReactNode,
} from 'react'
import { azoa, isOk } from './azoa'
import type { AuthProfile } from './azoa'

// ─── Types ───

export type WalletType = 'External' | 'Platform'

/** Normalize walletType from API — handles both string ("External"/"Platform") and integer (0/1) formats */
function normalizeWalletType(raw: unknown): WalletType {
  if (raw === 'External' || raw === 'Platform') return raw
  if (raw === 1) return 'Platform'
  return 'External' // covers 0, null, undefined, or any other value
}

function normalizeWalletEntry(w: any): WalletEntry {
  return {
    ...w,
    walletType: normalizeWalletType(w.walletType),
  }
}

export interface WalletEntry {
  id: string
  chainType: string
  address: string
  label?: string
  isDefault: boolean
  walletType: WalletType
  encryptedPrivateKey?: string
  encryptedSeedPhrase?: string
}

export interface WalletExportData {
  walletId: string
  chainType: string
  address: string
  publicKey?: string
  privateKey: string
  seedPhrase?: string
}

export interface AzoaState {
  user: AuthProfile | null
  isAuthenticated: boolean
  authLoading: boolean
  avatarId: string | null
  wallets: WalletEntry[]
  walletsLoading: boolean
  walletsError: string | null
  defaultWallet: WalletEntry | null
  login: (email: string, password: string) => Promise<{ success: boolean; error?: string }>
  register: (params: { username: string; email: string; password: string }) => Promise<{ success: boolean; error?: string }>
  logout: () => Promise<void>
  logoutEverywhere: () => Promise<{ success: boolean; error?: string }>
  refreshProfile: () => Promise<void>
  refreshWallets: () => Promise<void>
  setDefaultWallet: (walletId: string) => Promise<void>
  addWallet: (params: { chainType: string; address: string; label?: string; isDefault?: boolean }) => Promise<{ success: boolean; error?: string }>
  removeWallet: (walletId: string) => Promise<{ success: boolean; error?: string }>
  generateWallet: (params: { chainType: string; label?: string; isDefault?: boolean }) => Promise<{ success: boolean; error?: string }>
  connectExternalWallet: (params: { chainType: string; address: string; publicKey?: string; label?: string; isDefault?: boolean }) => Promise<{ success: boolean; error?: string }>
  exportWallet: (walletId: string) => Promise<{ success: boolean; data?: WalletExportData; error?: string }>
  browserWalletAvailable: (chainType: string) => boolean
  getBrowserWalletAddress: (chainType: string) => Promise<string | null>
  connectBrowserWallet: (chainType: string) => Promise<{ success: boolean; address?: string; error?: string }>
}

const AzoaContext = createContext<AzoaState | undefined>(undefined)
export { AzoaContext }

// ─── Provider ───

export function AzoaProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthProfile | null>(null)
  const [authLoading, setAuthLoading] = useState(true)
  const [wallets, setWallets] = useState<WalletEntry[]>([])
  const [walletsLoading, setWalletsLoading] = useState(false)
  const [walletsError, setWalletsError] = useState<string | null>(null)

  // Restore session on mount
  useEffect(() => {
    const restore = async () => {
      try {
        await azoa.session.restore()
        if (azoa.auth.isAuthenticated) {
          const profileResult = await azoa.auth.getProfile()
          if (isOk(profileResult)) {
            setUser(profileResult.value)
          } else {
            // Profile fetch failed (e.g., 401 — expired, 404 — deleted)
            // The SDK's 401 retry logic now handles refresh, but if it still fails,
            // we should clear the session.
            await azoa.auth.logout()
          }
        }
      } catch {
        await azoa.auth.logout().catch(() => {})
      } finally {
        setAuthLoading(false)
      }
    }
    restore()
  }, [])

  const avatarId = azoa.auth.avatarId

  const refreshWallets = useCallback(async () => {
    if (!avatarId) { setWallets([]); return }
    setWalletsLoading(true); setWalletsError(null)
    const result = await azoa.api.request<WalletEntry[]>('GET', `/api/wallet?avatarId=${avatarId}`)
    if (isOk(result)) setWallets(result.value.map(normalizeWalletEntry)); else setWalletsError(result.error.message)
    setWalletsLoading(false)
  }, [avatarId])

  useEffect(() => {
    if (avatarId) refreshWallets(); else setWallets([])
  }, [avatarId, refreshWallets])

  const refreshProfile = useCallback(async () => {
    if (!azoa.auth.isAuthenticated) return
    const r = await azoa.auth.getProfile()
    if (isOk(r)) setUser(r.value)
  }, [])

  /**
   * Login using the SDK's auth provider.
   */
  const login = useCallback(async (email: string, password: string) => {
    const result = await azoa.auth.login(email, password)
    if (isOk(result)) {
      const profileResult = await azoa.auth.getProfile()
      if (isOk(profileResult)) setUser(profileResult.value)
      return { success: true }
    }
    return { success: false, error: result.error.message }
  }, [])

  /**
   * Register + auto-login via SDK.
   */
  const register = useCallback(async (params: { username: string; email: string; password: string }) => {
    const result = await azoa.auth.register({
      username: params.username,
      email: params.email,
      password: params.password,
    })
    if (isOk(result)) {
      const profileResult = await azoa.auth.getProfile()
      if (isOk(profileResult)) setUser(profileResult.value)
      return { success: true }
    }
    return { success: false, error: result.error.message }
  }, [])

  const logout = useCallback(async () => {
    await azoa.auth.logout()
    setUser(null); setWallets([]); setWalletsError(null)
  }, [])

  /**
   * Server-side "logout everywhere": invalidates every live JWT for this
   * avatar, then clears the local session (same effect as `logout` locally).
   */
  const logoutEverywhere = useCallback(async () => {
    const result = await azoa.auth.logoutEverywhere()
    setUser(null); setWallets([]); setWalletsError(null)
    if (isOk(result)) return { success: true }
    return { success: false, error: result.error.message }
  }, [])

  const setDefaultWallet = useCallback(async (walletId: string) => {
    const result = await azoa.api.request('POST', `/api/wallet/${walletId}/set-default`)
    if (isOk(result)) await refreshWallets()
  }, [refreshWallets])

  const addWallet = useCallback(async (params: { chainType: string; address: string; label?: string; isDefault?: boolean }) => {
    if (!avatarId) return { success: false, error: 'No avatar ID' }
    const result = await azoa.api.request('POST', '/api/wallet', {
      avatarId, chainType: params.chainType, address: params.address.trim(),
      label: params.label?.trim() || undefined, isDefault: params.isDefault ?? false,
      walletType: 'External',
    })
    if (isOk(result)) { await refreshWallets(); return { success: true } }
    return { success: false, error: result.error.message }
  }, [avatarId, refreshWallets])

  const removeWallet = useCallback(async (walletId: string) => {
    const result = await azoa.api.request('DELETE', `/api/wallet/${walletId}`)
    if (isOk(result)) { await refreshWallets(); return { success: true } }
    return { success: false, error: result.error.message }
  }, [refreshWallets])

  // ─── New: Generate wallet on-platform ───

  const generateWallet = useCallback(async (params: { chainType: string; label?: string; isDefault?: boolean }) => {
    if (!avatarId) return { success: false, error: 'No avatar ID' }
    const result = await azoa.api.request('POST', '/api/wallet/generate', {
      chainType: params.chainType,
      label: params.label?.trim() || undefined,
      isDefault: params.isDefault ?? false,
    })
    if (isOk(result)) { await refreshWallets(); return { success: true } }
    return { success: false, error: result.error.message }
  }, [avatarId, refreshWallets])

  // ─── New: Connect external wallet ───

  const connectExternalWallet = useCallback(async (params: { chainType: string; address: string; publicKey?: string; label?: string; isDefault?: boolean }) => {
    if (!avatarId) return { success: false, error: 'No avatar ID' }
    const result = await azoa.api.request('POST', '/api/wallet/connect', {
      chainType: params.chainType,
      address: params.address.trim(),
      publicKey: params.publicKey,
      label: params.label?.trim() || undefined,
      isDefault: params.isDefault ?? false,
    })
    if (isOk(result)) { await refreshWallets(); return { success: true } }
    return { success: false, error: result.error.message }
  }, [avatarId, refreshWallets])

  // ─── New: Export wallet private key ───

  const exportWallet = useCallback(async (walletId: string): Promise<{ success: boolean; data?: WalletExportData; error?: string }> => {
    const result = await azoa.api.request<WalletExportData>('POST', `/api/wallet/${walletId}/export`)
    if (isOk(result)) return { success: true, data: result.value }
    return { success: false, error: result.error.message }
  }, [])

  // ─── Browser wallet detection & connection ───

  const browserWalletAvailable = useCallback((chainType: string): boolean => {
    if (typeof window === 'undefined') return false
    const ct = chainType.toLowerCase()
    if (ct === 'ethereum' || ct === 'eth') {
      return typeof (window as any).ethereum !== 'undefined'
    }
    if (ct === 'algorand' || ct === 'algo') {
      return typeof (window as any).algorand !== 'undefined' ||
             typeof (window as any).myAlgoWallet !== 'undefined' ||
             typeof (window as any).PeraWallet !== 'undefined'
    }
    if (ct === 'solana') {
      return typeof (window as any).solana !== 'undefined' ||
             typeof (window as any).phantom !== 'undefined'
    }
    return false
  }, [])

  const getBrowserWalletAddress = useCallback(async (chainType: string): Promise<string | null> => {
    if (typeof window === 'undefined') return null
    const ct = chainType.toLowerCase()

    try {
      if (ct === 'ethereum' || ct === 'eth') {
        const eth = (window as any).ethereum
        if (!eth) return null
        const accounts: string[] = await eth.request({ method: 'eth_accounts' })
        return accounts?.[0] ?? null
      }
      if (ct === 'algorand' || ct === 'algo') {
        const algo = (window as any).algorand
        if (algo?.accounts?.length) return algo.accounts[0]
        if ((window as any).PeraWallet) return null // need to connect first
        return null
      }
      if (ct === 'solana') {
        const sol = (window as any).solana ?? (window as any).phantom
        if (!sol) return null
        if (sol.publicKey) return sol.publicKey.toString()
        // Some wallets expose connected address differently
        const resp = await sol.connect({ onlyIfTrusted: true }).catch(() => null)
        return resp?.publicKey?.toString() ?? null
      }
    } catch { return null }
    return null
  }, [])

  const connectBrowserWallet = useCallback(async (chainType: string): Promise<{ success: boolean; address?: string; error?: string }> => {
    if (typeof window === 'undefined') return { success: false, error: 'Not in browser' }
    const ct = chainType.toLowerCase()

    try {
      if (ct === 'ethereum' || ct === 'eth') {
        const eth = (window as any).ethereum
        if (!eth) return { success: false, error: 'No Ethereum wallet detected (MetaMask, etc.)' }
        const accounts: string[] = await eth.request({ method: 'eth_requestAccounts' })
        if (!accounts?.[0]) return { success: false, error: 'No accounts returned' }
        return { success: true, address: accounts[0] }
      }
      if (ct === 'algorand' || ct === 'algo') {
        const algo = (window as any).algorand
        if (algo?.connect) {
          const result = await algo.connect()
          return { success: true, address: result?.address ?? result?.accounts?.[0] }
        }
        // PeraWallet / MyAlgo
        const pera = (window as any).PeraWallet
        if (pera?.connect) {
          const result = await pera.connect()
          return { success: true, address: result?.address ?? result?.accounts?.[0] }
        }
        return { success: false, error: 'No Algorand wallet detected (Pera, MyAlgo, etc.)' }
      }
      if (ct === 'solana') {
        const sol = (window as any).solana ?? (window as any).phantom
        if (!sol?.connect) return { success: false, error: 'No Solana wallet detected (Phantom, Solflare, etc.)' }
        const result = await sol.connect()
        return { success: true, address: result?.publicKey?.toString() }
      }
      return { success: false, error: `Unsupported chain: ${chainType}` }
    } catch (e: unknown) {
      return { success: false, error: e instanceof Error ? e.message : 'Connection failed' }
    }
  }, [])

  const defaultWallet = useMemo(() => wallets.find(w => w.isDefault) ?? wallets[0] ?? null, [wallets])

  const value = useMemo<AzoaState>(() => ({
    user, isAuthenticated: azoa.auth.isAuthenticated, authLoading, avatarId,
    wallets, walletsLoading, walletsError, defaultWallet,
    login, register, logout, logoutEverywhere, refreshProfile, refreshWallets, setDefaultWallet, addWallet, removeWallet,
    generateWallet, connectExternalWallet, exportWallet,
    browserWalletAvailable, getBrowserWalletAddress, connectBrowserWallet,
  }), [user, authLoading, avatarId, wallets, walletsLoading, walletsError, defaultWallet, login, register, logout, logoutEverywhere, refreshProfile, refreshWallets, setDefaultWallet, addWallet, removeWallet, generateWallet, connectExternalWallet, exportWallet, browserWalletAvailable, getBrowserWalletAddress, connectBrowserWallet])

  return <AzoaContext.Provider value={value}>{children}</AzoaContext.Provider>
}

export function useAzoa() {
  const ctx = useContext(AzoaContext)
  if (!ctx) throw new Error('useAzoa must be used within an AzoaProvider')
  return ctx
}

export function useWalletForChain(chain?: string) {
  const { wallets, defaultWallet } = useAzoa()
  if (!chain) return defaultWallet
  return wallets.find(w => w.chainType.toLowerCase() === chain.toLowerCase()) ?? defaultWallet
}
