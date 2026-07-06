/**
 * Catalog of built-in Quest node types and helpers for the visual builder.
 *
 * The list mirrors the backend `QuestNodeType` enum (Models/Quest/QuestEnums.cs).
 * The palette in the builder is driven by Node Templates fetched from the API
 * (which carry a `configSchema` / `defaultConfig`), falling back to this static
 * catalog when no matching template exists.
 */

export type NodeCategory =
  | 'Holon'
  | 'NFT'
  | 'Wallet'
  | 'STAR'
  | 'Search'
  | 'Avatar'
  | 'Blockchain'
  | 'Control'
  | 'Economic'

export interface NodeTypeMeta {
  /** Matches the backend QuestNodeType enum name. */
  type: string
  category: NodeCategory
  /** Short human label for the palette. */
  label: string
  /** One-line description shown as a tooltip / palette subtitle. */
  description: string
  /** Default JSON config seeded when a node of this type is added. */
  defaultConfig: string
  /** True for nodes that require an on-chain capability (Tier-2 economic). */
  requiresChain?: boolean
}

const J = (o: Record<string, unknown>) => JSON.stringify(o, null, 2)

export const NODE_CATALOG: NodeTypeMeta[] = [
  // ─── Holon ───
  { type: 'HolonCreate', category: 'Holon', label: 'Create Holon', description: 'Create a new holon', defaultConfig: J({ name: 'NewHolon', holonType: 'Holon' }) },
  { type: 'HolonUpdate', category: 'Holon', label: 'Update Holon', description: 'Update an existing holon', defaultConfig: J({ id: '', fields: {} }) },
  { type: 'HolonDelete', category: 'Holon', label: 'Delete Holon', description: 'Delete a holon', defaultConfig: J({ id: '' }) },
  { type: 'HolonGet', category: 'Holon', label: 'Get Holon', description: 'Fetch a holon by id', defaultConfig: J({ id: '' }) },
  { type: 'HolonQuery', category: 'Holon', label: 'Query Holons', description: 'Query holons by criteria', defaultConfig: J({ filter: {} }) },
  { type: 'HolonInteract', category: 'Holon', label: 'Interact', description: 'Invoke a holon interaction', defaultConfig: J({ id: '', action: '' }) },
  { type: 'HolonGetChildren', category: 'Holon', label: 'Get Children', description: 'Fetch child holons', defaultConfig: J({ id: '' }) },
  { type: 'HolonGetPeers', category: 'Holon', label: 'Get Peers', description: 'Fetch peer holons', defaultConfig: J({ id: '' }) },
  { type: 'HolonGetAncestors', category: 'Holon', label: 'Get Ancestors', description: 'Fetch ancestor holons', defaultConfig: J({ id: '' }) },
  { type: 'HolonGetDescendants', category: 'Holon', label: 'Get Descendants', description: 'Fetch descendant holons', defaultConfig: J({ id: '' }) },
  { type: 'HolonPropagate', category: 'Holon', label: 'Propagate', description: 'Propagate a change through the tree', defaultConfig: J({ id: '' }) },
  { type: 'HolonCompose', category: 'Holon', label: 'Compose', description: 'Compose holons together', defaultConfig: J({ ids: [] }) },
  { type: 'HolonClone', category: 'Holon', label: 'Clone', description: 'Clone a holon', defaultConfig: J({ id: '' }) },
  { type: 'HolonMoveSubtree', category: 'Holon', label: 'Move Subtree', description: 'Re-parent a holon subtree', defaultConfig: J({ id: '', newParentId: '' }) },

  // ─── NFT ───
  { type: 'NftMint', category: 'NFT', label: 'Mint NFT', description: 'Mint a new NFT', defaultConfig: J({ name: '', metadata: {} }) },
  { type: 'NftTransfer', category: 'NFT', label: 'Transfer NFT', description: 'Transfer an NFT', defaultConfig: J({ nftId: '', to: '' }) },
  { type: 'NftBurn', category: 'NFT', label: 'Burn NFT', description: 'Burn an NFT', defaultConfig: J({ nftId: '' }) },
  { type: 'NftGet', category: 'NFT', label: 'Get NFT', description: 'Fetch an NFT', defaultConfig: J({ nftId: '' }) },
  { type: 'NftQuery', category: 'NFT', label: 'Query NFTs', description: 'Query NFTs', defaultConfig: J({ filter: {} }) },
  { type: 'NftGetMetadata', category: 'NFT', label: 'Get Metadata', description: 'Fetch NFT metadata', defaultConfig: J({ nftId: '' }) },

  // ─── Wallet ───
  { type: 'WalletCreate', category: 'Wallet', label: 'Create Wallet', description: 'Create a wallet', defaultConfig: J({ chainType: 'Algorand' }) },
  { type: 'WalletUpdate', category: 'Wallet', label: 'Update Wallet', description: 'Update a wallet', defaultConfig: J({ id: '', label: '' }) },
  { type: 'WalletDelete', category: 'Wallet', label: 'Delete Wallet', description: 'Delete a wallet', defaultConfig: J({ id: '' }) },
  { type: 'WalletGet', category: 'Wallet', label: 'Get Wallet', description: 'Fetch a wallet', defaultConfig: J({ id: '' }) },
  { type: 'WalletQuery', category: 'Wallet', label: 'Query Wallets', description: 'Query wallets', defaultConfig: J({ filter: {} }) },
  { type: 'WalletSetDefault', category: 'Wallet', label: 'Set Default', description: 'Set the default wallet', defaultConfig: J({ id: '' }) },
  { type: 'WalletGetPortfolio', category: 'Wallet', label: 'Get Portfolio', description: 'Aggregate wallet portfolio', defaultConfig: J({ id: '' }) },

  // ─── STAR ───
  { type: 'StarGenerate', category: 'STAR', label: 'Generate STAR', description: 'Generate a STAR dapp', defaultConfig: J({ name: '' }) },
  { type: 'StarDeploy', category: 'STAR', label: 'Deploy STAR', description: 'Deploy a STAR dapp', defaultConfig: J({ id: '' }) },

  // ─── Search ───
  { type: 'Search', category: 'Search', label: 'Search', description: 'Run a search query', defaultConfig: J({ query: '' }) },

  // ─── Avatar ───
  { type: 'AvatarNFTGetComposite', category: 'Avatar', label: 'Composite Avatar NFT', description: 'Fetch composite avatar NFT', defaultConfig: J({ avatarId: '' }) },

  // ─── Blockchain ───
  { type: 'BlockchainExecute', category: 'Blockchain', label: 'Execute On-chain', description: 'Execute a raw blockchain operation', defaultConfig: J({ chain: '', operation: '' }) },

  // ─── Control ───
  { type: 'Condition', category: 'Control', label: 'Condition', description: 'Legacy branch (prefer GateCheck)', defaultConfig: J({ expression: '' }) },
  { type: 'ComposeOutputs', category: 'Control', label: 'Compose Outputs', description: 'Merge outputs of upstream nodes', defaultConfig: J({ sources: [] }) },
  { type: 'GateCheck', category: 'Control', label: 'Gate Check', description: 'Tier-1 gate predicate (supersedes Condition)', defaultConfig: J({ expression: '' }) },
  // eventType (final-hardening F3) is the free-form quest.emit webhook event name;
  // it is a top-level sibling of payload on the wire (Models/Quest/NodeConfigs.cs
  // EmitNodeConfig.EventType), NOT nested inside payload. Defaults server-side to
  // "quest.emit" when omitted.
  { type: 'Emit', category: 'Control', label: 'Emit', description: 'Emit a settlement event to the tenant', defaultConfig: J({ eventType: '', payload: {} }) },

  // ─── Economic (Tier-2, requires chain capability) ───
  { type: 'Swap', category: 'Economic', label: 'Swap', description: 'On-chain token swap', defaultConfig: J({ from: '', to: '', amount: '' }), requiresChain: true },
  { type: 'Grant', category: 'Economic', label: 'Grant', description: 'Grant tokens / rights', defaultConfig: J({ to: '', amount: '' }), requiresChain: true },
  { type: 'Transfer', category: 'Economic', label: 'Transfer', description: 'Transfer value on-chain', defaultConfig: J({ to: '', amount: '' }), requiresChain: true },
  { type: 'Refund', category: 'Economic', label: 'Refund', description: 'Refund a prior transfer', defaultConfig: J({ ref: '' }), requiresChain: true },
  { type: 'FungibleTokenCreate', category: 'Economic', label: 'Create Fungible Token', description: 'Launch a fungible token (ASA) backed/linked to an asset holon', defaultConfig: J({ chainType: 'Algorand', name: '', unitName: '', total: 1000000, decimals: 6, holonId: null }), requiresChain: true },
  // Fractionalization rails (final-hardening D1): both route through the REAL
  // cross-chain bridge (Algorand real, Solana fail-closed). The node moves value
  // only — peg/valuation is tenant-side (compute it and hand it off via Emit).
  { type: 'Bridge', category: 'Economic', label: 'Bridge', description: 'Lock/bridge an asset cross-chain (real bridge; peg stays tenant-side)', defaultConfig: J({ sourceChain: 'Algorand', targetChain: '', tokenId: '', recipientAddress: '', amount: 1, mode: null }), requiresChain: true },
  { type: 'Back', category: 'Economic', label: 'Back (Reverse Bridge)', description: 'Reverse a prior Bridge: burn wrapped on target, release original on source', defaultConfig: J({ bridgeTransactionId: '', sourceRecipientAddress: '' }), requiresChain: true },
]

export const NODE_CATALOG_BY_TYPE: Record<string, NodeTypeMeta> = Object.fromEntries(
  NODE_CATALOG.map((n) => [n.type, n]),
)

export const CATEGORY_ORDER: NodeCategory[] = [
  'Holon', 'NFT', 'Wallet', 'STAR', 'Search', 'Avatar', 'Blockchain', 'Control', 'Economic',
]

/** Tailwind classes for the colored left-border / accent per category. */
export const CATEGORY_COLOR: Record<NodeCategory, { border: string; badge: string; dot: string }> = {
  Holon: { border: 'border-l-blue-500', badge: 'bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-300', dot: 'bg-blue-500' },
  NFT: { border: 'border-l-purple-500', badge: 'bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-300', dot: 'bg-purple-500' },
  Wallet: { border: 'border-l-emerald-500', badge: 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-300', dot: 'bg-emerald-500' },
  STAR: { border: 'border-l-amber-500', badge: 'bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-300', dot: 'bg-amber-500' },
  Search: { border: 'border-l-cyan-500', badge: 'bg-cyan-100 text-cyan-800 dark:bg-cyan-900/30 dark:text-cyan-300', dot: 'bg-cyan-500' },
  Avatar: { border: 'border-l-pink-500', badge: 'bg-pink-100 text-pink-800 dark:bg-pink-900/30 dark:text-pink-300', dot: 'bg-pink-500' },
  Blockchain: { border: 'border-l-orange-500', badge: 'bg-orange-100 text-orange-800 dark:bg-orange-900/30 dark:text-orange-300', dot: 'bg-orange-500' },
  Control: { border: 'border-l-slate-500', badge: 'bg-slate-100 text-slate-800 dark:bg-slate-800 dark:text-slate-300', dot: 'bg-slate-500' },
  Economic: { border: 'border-l-rose-500', badge: 'bg-rose-100 text-rose-800 dark:bg-rose-900/30 dark:text-rose-300', dot: 'bg-rose-500' },
}

/** Best-effort category lookup for an arbitrary node type string. */
export function categoryFor(nodeType: string): NodeCategory {
  return NODE_CATALOG_BY_TYPE[nodeType]?.category ?? 'Control'
}
