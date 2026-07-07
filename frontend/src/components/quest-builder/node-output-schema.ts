/**
 * Client-side mirror of the backend output-field taxonomy for `$from` binding
 * PRESENCE checks (ancestry is checked separately in quest-canvas.tsx).
 *
 * Authority: Services/Quest/QuestNodeOutputSchema.cs (QuestNodeOutputSchema /
 * NodeOutputShape). This mirror is best-effort and non-authoritative — the
 * server re-validates. Only field NAMES are mirrored (case-insensitive
 * match); scalar type-matching (backend rule C) is intentionally SKIPPED
 * client-side.
 */

/** Whether a matched field admits deeper `.a.b.c` segments without further checks. */
export type FieldDepth = 'deep' | 'scalar'

export interface NodeOutputShapeMirror {
  /** true = opaque/free-form output; presence check is skipped entirely. */
  open: boolean
  /** Declared top-level field name (any case) → whether it admits deep paths. */
  fields: Record<string, FieldDepth>
}

const OPEN: NodeOutputShapeMirror = { open: true, fields: {} }

/** Top-level shape of a serialized `AZOAResult<T>` wrapper; Result's depth varies by T. */
function wrapped(resultDepth: FieldDepth): NodeOutputShapeMirror {
  return {
    open: false,
    fields: {
      IsError: 'scalar',
      Message: 'scalar',
      Result: resultDepth,
      Detail: 'deep', // Object
    },
  }
}

const WRAPPED_OBJECT = wrapped('deep') // Result: Object
const WRAPPED_ARRAY = wrapped('deep') // Result: Array — also admits deep paths (backend admits Object|Array)
const WRAPPED_BOOL = wrapped('scalar') // Result: Boolean
const WRAPPED_NUMBER = wrapped('scalar') // Result: Number

/** Flat BridgeTransactionResult shape — Bridge/Back serialize r.Result directly (no wrapper). */
const BRIDGE_TRANSACTION_SHAPE: NodeOutputShapeMirror = {
  open: false,
  fields: {
    Id: 'scalar',
    AvatarId: 'scalar',
    SourceChain: 'scalar',
    TargetChain: 'scalar',
    SourceTokenId: 'scalar',
    TargetTokenId: 'scalar',
    SourceAddress: 'scalar',
    TargetAddress: 'scalar',
    Amount: 'scalar',
    Status: 'scalar',
    Mode: 'scalar',
    LockTxHash: 'scalar',
    MintTxHash: 'scalar',
    ProofData: 'scalar',
    ErrorMessage: 'scalar',
    CreatedAt: 'scalar',
    CompletedAt: 'scalar',
    WormholeEmitterChainId: 'scalar',
    WormholeEmitterAddress: 'scalar',
    WormholeSequence: 'scalar',
    VaaBytes: 'scalar',
    VaaSignatureCount: 'scalar',
    RedemptionTxHash: 'scalar',
    IdempotencyKey: 'scalar',
    Network: 'scalar',
  },
}

const GATE_CHECK_SHAPE: NodeOutputShapeMirror = {
  open: false,
  fields: { pass: 'scalar' },
}

/** nodeType (matches NODE_CATALOG `type`) → output shape mirror. */
export const NODE_OUTPUT_SHAPE: Record<string, NodeOutputShapeMirror> = {
  // ── Holon operations ──
  HolonCreate: WRAPPED_OBJECT,
  HolonUpdate: WRAPPED_OBJECT,
  HolonGet: WRAPPED_OBJECT,
  HolonInteract: WRAPPED_OBJECT,
  HolonClone: WRAPPED_OBJECT,
  HolonDelete: WRAPPED_BOOL,
  HolonMoveSubtree: WRAPPED_BOOL,
  HolonPropagate: WRAPPED_NUMBER,
  HolonQuery: WRAPPED_ARRAY,
  HolonGetChildren: WRAPPED_ARRAY,
  HolonGetPeers: WRAPPED_ARRAY,
  HolonGetAncestors: WRAPPED_ARRAY,
  HolonGetDescendants: WRAPPED_ARRAY,
  HolonCompose: WRAPPED_OBJECT,

  // ── NFT operations ──
  NftMint: WRAPPED_OBJECT,
  NftTransfer: WRAPPED_OBJECT,
  NftBurn: WRAPPED_OBJECT,
  NftGet: WRAPPED_OBJECT,
  NftQuery: WRAPPED_ARRAY,
  NftGetMetadata: WRAPPED_OBJECT,

  // ── Wallet operations ──
  WalletCreate: WRAPPED_OBJECT,
  WalletUpdate: WRAPPED_OBJECT,
  WalletGet: WRAPPED_OBJECT,
  WalletDelete: WRAPPED_BOOL,
  WalletSetDefault: WRAPPED_BOOL,
  WalletQuery: WRAPPED_ARRAY,
  WalletGetPortfolio: WRAPPED_OBJECT,

  // ── STAR ──
  StarGenerate: WRAPPED_OBJECT,
  StarDeploy: WRAPPED_OBJECT,

  // ── Search ──
  Search: WRAPPED_OBJECT,

  // ── Avatar NFT ──
  AvatarNFTGetComposite: WRAPPED_OBJECT,

  // ── Blockchain ──
  BlockchainExecute: WRAPPED_OBJECT,

  // ── Internal / control-flow (open — dynamic/free-form) ──
  Condition: OPEN,
  ComposeOutputs: OPEN,
  Emit: OPEN,

  // ── Holon-transformation nodes ──
  GateCheck: GATE_CHECK_SHAPE,

  // ── Tier-2 economic nodes ──
  Swap: WRAPPED_OBJECT,
  Grant: WRAPPED_OBJECT,
  Transfer: WRAPPED_OBJECT,
  Refund: WRAPPED_OBJECT,
  FungibleTokenCreate: WRAPPED_OBJECT,

  // ── Fractionalization rails — flat BridgeTransactionResult ──
  Bridge: BRIDGE_TRANSACTION_SHAPE,
  Back: BRIDGE_TRANSACTION_SHAPE,
}

/**
 * Looks up the mirrored shape for a node type. Unknown/unmapped types are
 * treated as open (skip presence) rather than erroring — this mirror must
 * never false-positive on a node type it doesn't recognize (e.g. a newly
 * added backend type this file hasn't been updated for yet).
 */
export function getOutputShapeMirror(nodeType: string): NodeOutputShapeMirror {
  return NODE_OUTPUT_SHAPE[nodeType] ?? OPEN
}
