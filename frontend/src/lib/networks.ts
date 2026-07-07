'use client'

/**
 * Shared network registry.
 *
 * RPC endpoints are OWNED BY THE BACKEND (appsettings.json → GET /api/network).
 * The frontend ships only safe public *fallbacks* so it works offline / before
 * the config call returns; once the backend config is fetched it overrides the
 * fallbacks (see network-context.tsx). A frontend developer never has to set
 * an RPC URL — change them in the backend's appsettings.json instead.
 *
 * Network selection itself is a GLOBAL, app-wide switch held by the SDK client
 * (azoa.network / azoa.setNetwork). Only the active network's providers are
 * ever registered, so operations can never span devnet/testnet/mainnet.
 */

import { AlgorandProvider, SolanaProvider } from 'azoa-sdk'
import type { ChainProviderRegistration, ChainNetwork } from 'azoa-sdk'

export type NetworkEnv = 'devnet' | 'testnet' | 'mainnet'

export const NETWORK_ENVS: readonly NetworkEnv[] = ['devnet', 'testnet', 'mainnet'] as const

export interface NetworkMeta {
  env: NetworkEnv
  label: string
  description: string
  /** devnet|testnet → faucet "Top up"; mainnet → "Buy" */
  isTestLike: boolean
  /** tailwind bg color for the status dot */
  dotClass: string
}

export const NETWORK_META: Record<NetworkEnv, NetworkMeta> = {
  devnet: {
    env: 'devnet',
    label: 'Devnet',
    description: 'Development network — free test tokens via faucet',
    isTestLike: true,
    dotClass: 'bg-amber-500',
  },
  testnet: {
    env: 'testnet',
    label: 'Testnet',
    description: 'Test network — free test tokens via faucet',
    isTestLike: true,
    dotClass: 'bg-sky-500',
  },
  mainnet: {
    env: 'mainnet',
    label: 'Mainnet',
    description: 'Production network — real funds; buy tokens via DEX',
    isTestLike: false,
    dotClass: 'bg-emerald-500',
  },
}

/** Last-resort default before the backend config is read (backend wins). */
export const DEFAULT_NETWORK: NetworkEnv =
  (process.env.NEXT_PUBLIC_DEFAULT_NETWORK as NetworkEnv) || 'testnet'

export function isNetworkEnv(v: unknown): v is NetworkEnv {
  return v === 'devnet' || v === 'testnet' || v === 'mainnet'
}

/** localStorage key for the persisted network choice (shared by all layers). */
export const NETWORK_STORAGE_KEY = 'azoa.network'

export function readInitialNetwork(): NetworkEnv {
  if (typeof window === 'undefined') return DEFAULT_NETWORK
  const stored = window.localStorage.getItem(NETWORK_STORAGE_KEY)
  return isNetworkEnv(stored) ? stored : DEFAULT_NETWORK
}

/** True only if the user has explicitly chosen a network (persisted). */
export function hasPersistedNetwork(): boolean {
  if (typeof window === 'undefined') return false
  return isNetworkEnv(window.localStorage.getItem(NETWORK_STORAGE_KEY))
}

// ─── Resolved endpoints (backend-overridable at runtime) ───

interface ResolvedEndpoints {
  solana: { rpcUrl: string }
  algorand: { algodUrl: string; indexerUrl: string }
}

// Public fallbacks ONLY. The backend (GET /api/network) is authoritative and
// overrides these at startup. No NEXT_PUBLIC_* RPC env vars by design.
const FALLBACK_ENDPOINTS: Record<NetworkEnv, ResolvedEndpoints> = {
  devnet: {
    solana: { rpcUrl: 'https://api.devnet.solana.com' },
    algorand: {
      algodUrl: 'https://testnet-api.algonode.cloud',
      indexerUrl: 'https://testnet-idx.algonode.cloud',
    },
  },
  testnet: {
    solana: { rpcUrl: 'https://api.testnet.solana.com' },
    algorand: {
      algodUrl: 'https://testnet-api.algonode.cloud',
      indexerUrl: 'https://testnet-idx.algonode.cloud',
    },
  },
  mainnet: {
    solana: { rpcUrl: 'https://api.mainnet-beta.solana.com' },
    algorand: {
      algodUrl: 'https://mainnet-api.algonode.cloud',
      indexerUrl: 'https://mainnet-idx.algonode.cloud',
    },
  },
}

function cloneEndpoints(
  src: Record<NetworkEnv, ResolvedEndpoints>
): Record<NetworkEnv, ResolvedEndpoints> {
  return {
    devnet: { solana: { ...src.devnet.solana }, algorand: { ...src.devnet.algorand } },
    testnet: { solana: { ...src.testnet.solana }, algorand: { ...src.testnet.algorand } },
    mainnet: { solana: { ...src.mainnet.solana }, algorand: { ...src.mainnet.algorand } },
  }
}

// Mutable, starts at fallbacks; replaced/merged by applyBackendNetworkConfig().
let runtimeEndpoints: Record<NetworkEnv, ResolvedEndpoints> = cloneEndpoints(FALLBACK_ENDPOINTS)

// ─── Backend /api/network response (mirrors .NET NetworkConfigResponse) ───

export interface BackendChainEndpoint {
  nodeUrl: string
  indexerUrl?: string | null
  isEnabled: boolean
  explorerUrl?: string | null
  nativeToken?: string | null
  decimals?: number | null
}

export interface BackendNetworkChains {
  algorand?: BackendChainEndpoint | null
  solana?: BackendChainEndpoint | null
  ethereum?: BackendChainEndpoint | null
}

export interface NetworkConfigResponse {
  defaultNetwork: string
  networks: Partial<Record<NetworkEnv, BackendNetworkChains>>
}

/**
 * Merge backend-provided RPC endpoints over the public fallbacks. Returns the
 * backend's preferred default network (validated), or null if unusable.
 * Safe to call repeatedly; only non-empty backend URLs override fallbacks.
 */
export function applyBackendNetworkConfig(resp: NetworkConfigResponse): NetworkEnv | null {
  const next = cloneEndpoints(FALLBACK_ENDPOINTS)
  for (const env of NETWORK_ENVS) {
    const chains = resp.networks?.[env]
    if (!chains) continue
    if (chains.solana?.nodeUrl) next[env].solana.rpcUrl = chains.solana.nodeUrl
    if (chains.algorand?.nodeUrl) next[env].algorand.algodUrl = chains.algorand.nodeUrl
    if (chains.algorand?.indexerUrl) next[env].algorand.indexerUrl = chains.algorand.indexerUrl
  }
  runtimeEndpoints = next
  return isNetworkEnv(resp.defaultNetwork) ? resp.defaultNetwork : null
}

/**
 * Build the SDK chain-provider registrations for `env` from the *current*
 * runtime endpoints. Used as the AzoaClient `chainsForNetwork` factory, so
 * setNetwork()/refreshChains() always pick up the latest (backend) URLs.
 */
export function buildChainRegistrations(
  env: NetworkEnv
): Record<string, ChainProviderRegistration> {
  const ep = runtimeEndpoints[env]

  // The SDK ChainNetwork union is devnet|testnet|mainnet. Solana supports all
  // three; Algorand (algonode) only has testnet|mainnet, so devnet → testnet.
  const algoNetwork: ChainNetwork = env === 'mainnet' ? 'mainnet' : 'testnet'

  return {
    algorand: {
      provider: new AlgorandProvider({
        rpcUrl: ep.algorand.algodUrl,
        algodUrl: ep.algorand.algodUrl,
        indexerUrl: ep.algorand.indexerUrl,
        network: algoNetwork,
      }),
    },
    solana: {
      provider: new SolanaProvider({
        rpcUrl: ep.solana.rpcUrl,
        network: env,
      }),
    },
  }
}

// ─── Project token config (for the mainnet "Buy" flow) ───
// Token identity is product config (not RPC); kept as env vars. Fill to enable
// the buy flow; "Buy" reuses the existing Jupiter/Tinyman swap endpoints.

export interface ProjectTokenConfig {
  symbol: string
  solanaMint?: string
  algorandAssetId?: string
  solanaPayWithMint: string
  algorandPayWithAssetId: string
}

export const PROJECT_TOKEN: ProjectTokenConfig = {
  symbol: process.env.NEXT_PUBLIC_BUY_TOKEN_SYMBOL || 'AZOA',
  solanaMint: process.env.NEXT_PUBLIC_BUY_TOKEN_SOLANA_MINT || undefined,
  algorandAssetId: process.env.NEXT_PUBLIC_BUY_TOKEN_ALGORAND_ASSET_ID || undefined,
  solanaPayWithMint:
    process.env.NEXT_PUBLIC_BUY_PAYWITH_SOLANA_MINT ||
    'So11111111111111111111111111111111111111112',
  algorandPayWithAssetId: process.env.NEXT_PUBLIC_BUY_PAYWITH_ALGORAND_ASSET_ID || '0',
}

export function getBuyTargetTokenId(chain: string): string | null {
  const c = chain.toLowerCase()
  if (c === 'solana') return PROJECT_TOKEN.solanaMint ?? null
  if (c === 'algorand') return PROJECT_TOKEN.algorandAssetId ?? null
  return null
}

export function getPayWithTokenId(chain: string): string {
  const c = chain.toLowerCase()
  if (c === 'solana') return PROJECT_TOKEN.solanaPayWithMint
  return PROJECT_TOKEN.algorandPayWithAssetId
}
