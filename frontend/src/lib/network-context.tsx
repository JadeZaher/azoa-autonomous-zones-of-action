'use client'

/**
 * NetworkProvider — global, app-wide network state (devnet / testnet / mainnet).
 *
 * Source of truth is the SDK client (`oasis.network` / `oasis.setNetwork`).
 * RPC endpoints are owned by the backend: on mount this fetches GET /api/network
 * and merges those URLs over the shipped public fallbacks, then rebuilds the
 * SDK providers. A network switch repoints every operation with no page reload
 * and without dropping the user session.
 *
 * `networkKey` changes whenever the active network OR the resolved endpoints
 * change; data hooks depend on it so they refetch against correct endpoints.
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
import { oasis, isOk } from './oasis'
import {
  type NetworkEnv,
  NETWORK_META,
  NETWORK_ENVS,
  NETWORK_STORAGE_KEY,
  readInitialNetwork,
  hasPersistedNetwork,
  applyBackendNetworkConfig,
  isNetworkEnv,
  type NetworkConfigResponse,
  type NetworkMeta,
} from './networks'

interface NetworkContextValue {
  network: NetworkEnv
  meta: NetworkMeta
  /** true on devnet/testnet (faucet top-up), false on mainnet (buy) */
  isTestLike: boolean
  /** Stable key for data-hook deps: changes on switch OR endpoint config load. */
  networkKey: string
  /** All selectable networks, in display order. */
  networks: readonly NetworkEnv[]
  /** Switch the active network (repoints the SDK, no reload). */
  setNetwork: (env: NetworkEnv) => void
}

const NetworkContext = createContext<NetworkContextValue | undefined>(undefined)
export { NetworkContext }

export function NetworkProvider({ children }: { children: ReactNode }) {
  const [network, setNetworkState] = useState<NetworkEnv>(() => readInitialNetwork())
  // Bumped when backend endpoint config is applied so hooks refetch.
  const [revision, setRevision] = useState(0)

  const setNetwork = useCallback(
    (env: NetworkEnv) => {
      if (env === network) return
      oasis.setNetwork(env) // rebuilds providers for `env` (only its providers exist)
      if (typeof window !== 'undefined') {
        window.localStorage.setItem(NETWORK_STORAGE_KEY, env)
      }
      setNetworkState(env)
    },
    [network]
  )

  // Fetch backend-owned RPC config once on mount; merge over fallbacks.
  useEffect(() => {
    let cancelled = false
    ;(async () => {
      const res = await oasis.api.request<NetworkConfigResponse>('GET', '/api/network')
      if (cancelled || !isOk(res)) return // keep public fallbacks on failure
      const backendDefault = applyBackendNetworkConfig(res.value)
      // Rebuild providers for the CURRENT network with backend URLs.
      oasis.refreshChains()
      setRevision((r) => r + 1)
      // Adopt the backend's default only if the user hasn't chosen one.
      if (
        !hasPersistedNetwork() &&
        isNetworkEnv(backendDefault) &&
        backendDefault !== network
      ) {
        oasis.setNetwork(backendDefault)
        setNetworkState(backendDefault)
      }
    })()
    return () => {
      cancelled = true
    }
  }, []) // once

  const value = useMemo<NetworkContextValue>(
    () => ({
      network,
      meta: NETWORK_META[network],
      isTestLike: NETWORK_META[network].isTestLike,
      networkKey: `${network}#${revision}`,
      networks: NETWORK_ENVS,
      setNetwork,
    }),
    [network, revision, setNetwork]
  )

  return <NetworkContext.Provider value={value}>{children}</NetworkContext.Provider>
}

export function useNetwork(): NetworkContextValue {
  const ctx = useContext(NetworkContext)
  if (!ctx) throw new Error('useNetwork must be used within a NetworkProvider')
  return ctx
}
