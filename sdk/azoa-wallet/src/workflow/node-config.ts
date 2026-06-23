/**
 * Typed `nodeConfig` builders — pure helpers that serialize the **generic
 * mechanism** params of the `economic-primitive-nodes` node types into the
 * `Config` JSON string a `QuestNode` / template node carries on the wire.
 *
 * Each builder mirrors the corresponding C# config POCO in
 * `Models/Quest/NodeConfigs.cs` exactly. They type ONLY generic mechanism params —
 * amounts stay STRINGS, and there is NO rate, NO token meaning, NO ArdaNova
 * economic concept anywhere here (NFR-4). For any node kind the SDK has not typed
 * (forward-compat), {@link nodeConfig.raw} accepts a pre-built object/string as an
 * escape hatch.
 *
 * The builders are pure (no I/O) so they cost nothing at runtime and tree-shake
 * away when unused.
 */

/**
 * GateCheck config. Mirrors `GateCheckNodeConfig { string Predicate;
 * Dictionary<string, JsonElement> Reads }`. `predicate` is a whitelisted boolean
 * expression over upstream outputs (`upstream.<nodeName>.<jsonPath>`) and injected
 * reads (`reads.<name>`); `reads` supplies the tenant-injected read values by
 * name. AZOA only compares — no economics.
 */
export interface GateCheckConfig {
  predicate: string;
  reads?: Record<string, unknown>;
}

/**
 * Emit config. Mirrors `EmitNodeConfig { JsonElement Payload }`. `payload` is an
 * opaque tenant-shaped value serialized to the node's output (AZOA holds no
 * settlement/fiat/payout state — the tenant settles).
 */
export interface EmitConfig {
  payload: unknown;
}

/**
 * Swap config. Mirrors `SwapNodeConfig { SwapExecuteRequest Request }`, whose
 * request is `{ Chain, QuoteId, WalletAddress }`. The rate comes from the DEX,
 * never AZOA — this only carries the mechanism params.
 */
export interface SwapConfig {
  request: {
    /** Chain identifier, e.g. "algorand" / "solana". */
    chain: string;
    /** The QuoteId returned by the swap-quote endpoint. */
    quoteId: string;
    /** Public key of the wallet that will sign the swap transaction. */
    walletAddress: string;
  };
}

/** A generic NFT mint request (`NftMintRequest`). Amounts/ids are strings. */
export interface NftMintRequestParams {
  walletId: string;
  name: string;
  description?: string;
  chainId: string;
  tokenId?: string;
  imageUri?: string;
  externalUri?: string;
  metadata?: Record<string, string>;
}

/** A generic NFT transfer request (`NftTransferRequest`). */
export interface NftTransferRequestParams {
  targetAvatarId: string;
  walletId: string;
  memo?: string;
}

/**
 * Grant (mint-to-actor) config. Mirrors `GrantNodeConfig { NftMintRequest
 * Request; Guid? HolonId }`. The actor avatar is taken from the run context,
 * never this body.
 */
export interface GrantConfig {
  request: NftMintRequestParams;
  /** Optional holon to link to the minted asset. */
  holonId?: string;
}

/**
 * Transfer (move-to-actor) config. Mirrors `TransferNodeConfig { Guid NftId;
 * NftTransferRequest Request }`. Actor avatar from run context.
 */
export interface TransferConfig {
  nftId: string;
  request: NftTransferRequestParams;
}

/**
 * Refund (reverse transfer / clawback-deferred) config. Mirrors
 * `RefundNodeConfig { Guid NftId; NftTransferRequest Request }`. Actor from run
 * context.
 */
export interface RefundConfig {
  nftId: string;
  request: NftTransferRequestParams;
}

function buildNftMintRequest(p: NftMintRequestParams): Record<string, unknown> {
  return {
    walletId: p.walletId,
    name: p.name,
    description: p.description ?? "",
    chainId: p.chainId,
    ...(p.tokenId !== undefined ? { tokenId: p.tokenId } : {}),
    ...(p.imageUri !== undefined ? { imageUri: p.imageUri } : {}),
    ...(p.externalUri !== undefined ? { externalUri: p.externalUri } : {}),
    metadata: p.metadata ?? {},
  };
}

function buildNftTransferRequest(
  p: NftTransferRequestParams
): Record<string, unknown> {
  return {
    targetAvatarId: p.targetAvatarId,
    walletId: p.walletId,
    ...(p.memo !== undefined ? { memo: p.memo } : {}),
  };
}

/**
 * Pure typed builders for the generic node `Config` JSON string. Each returns the
 * serialized string the template/quest node DTO expects on the wire; `raw` is the
 * forward-compat escape hatch for un-typed node kinds.
 */
export const nodeConfig = {
  /** Serialize a {@link GateCheckConfig} to its `Config` string. */
  gateCheck(config: GateCheckConfig): string {
    return JSON.stringify({
      predicate: config.predicate,
      reads: config.reads ?? {},
    });
  },

  /** Serialize an {@link EmitConfig} to its `Config` string. */
  emit(config: EmitConfig): string {
    return JSON.stringify({ payload: config.payload });
  },

  /** Serialize a {@link SwapConfig} to its `Config` string. */
  swap(config: SwapConfig): string {
    return JSON.stringify({
      request: {
        chain: config.request.chain,
        quoteId: config.request.quoteId,
        walletAddress: config.request.walletAddress,
      },
    });
  },

  /** Serialize a {@link GrantConfig} to its `Config` string. */
  grant(config: GrantConfig): string {
    return JSON.stringify({
      request: buildNftMintRequest(config.request),
      ...(config.holonId !== undefined ? { holonId: config.holonId } : {}),
    });
  },

  /** Serialize a {@link TransferConfig} to its `Config` string. */
  transfer(config: TransferConfig): string {
    return JSON.stringify({
      nftId: config.nftId,
      request: buildNftTransferRequest(config.request),
    });
  },

  /** Serialize a {@link RefundConfig} to its `Config` string. */
  refund(config: RefundConfig): string {
    return JSON.stringify({
      nftId: config.nftId,
      request: buildNftTransferRequest(config.request),
    });
  },

  /**
   * Escape hatch: accept a pre-built config object (serialized here) or an
   * already-serialized `Config` string (passed through unchanged). Use for node
   * kinds the SDK has not yet typed.
   */
  raw(config: string | Record<string, unknown>): string {
    return typeof config === "string" ? config : JSON.stringify(config);
  },
} as const;
