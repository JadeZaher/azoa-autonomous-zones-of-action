import type {
  ChainProvider,
  ChainProviderConfig,
  BalanceInfo,
  AssetInfo,
  TransactionResult,
  UnsignedTransaction,
  TransferParams,
  MintParams,
  BurnParams,
  Signer,
} from "../core/types.js";
import type { Result } from "../core/result.js";
import { ok, err } from "../core/result.js";
import { SdkError, SdkErrorCode } from "../core/errors.js";

// Optional peer dependency: @noble/curves for Ed25519 signing.
// Dynamically imported so the SDK works without it — wallet adapters that
// handle their own signing can operate with the json-descriptor format.
// The type is declared inline to avoid a compile-time module resolution error
// when @noble/curves is not installed.
type NobleEd25519 = { sign(msg: Uint8Array, privKey: Uint8Array): Uint8Array };
let _ed25519: NobleEd25519 | null = null;
let _ed25519LoadAttempted = false;

async function getEd25519(): Promise<NobleEd25519 | null> {
  if (_ed25519LoadAttempted) return _ed25519;
  _ed25519LoadAttempted = true;
  try {
    // Dynamic import — will throw/reject when @noble/curves is not installed,
    // which we catch and treat as "not available".
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const mod = (await import("@noble/curves/ed25519" as string)) as any;
    // eslint-disable-next-line @typescript-eslint/no-unsafe-member-access
    _ed25519 = mod.ed25519 as NobleEd25519;
  } catch {
    // @noble/curves not installed — JSON-descriptor mode only
    _ed25519 = null;
  }
  return _ed25519;
}

// ─── Cross-platform base64 helpers ───────────────────────────────────────────
// Pure JS — no btoa/atob which are unavailable in React Native / older Node.

const B64_CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

function bytesToBase64(bytes: Uint8Array): string {
  let result = "";
  for (let i = 0; i < bytes.length; i += 3) {
    const b0 = bytes[i]!;
    const b1 = bytes[i + 1] ?? 0;
    const b2 = bytes[i + 2] ?? 0;
    result += B64_CHARS[b0 >> 2]!;
    result += B64_CHARS[((b0 & 3) << 4) | (b1 >> 4)]!;
    result += i + 1 < bytes.length ? B64_CHARS[((b1 & 15) << 2) | (b2 >> 6)]! : "=";
    result += i + 2 < bytes.length ? B64_CHARS[b2 & 63]! : "=";
  }
  return result;
}

function base64ToBytes(b64: string): Uint8Array {
  const lookup = new Uint8Array(256).fill(255);
  for (let i = 0; i < B64_CHARS.length; i++) lookup[B64_CHARS.charCodeAt(i)] = i;
  const s = b64.replace(/[\s=]+/g, "");
  const out = new Uint8Array(Math.floor((s.length * 3) / 4));
  let idx = 0;
  for (let i = 0; i < s.length; i += 4) {
    const a = lookup[s.charCodeAt(i)]!;
    const b = lookup[s.charCodeAt(i + 1)]!;
    const c = i + 2 < s.length ? lookup[s.charCodeAt(i + 2)]! : 0;
    const d = i + 3 < s.length ? lookup[s.charCodeAt(i + 3)]! : 0;
    out[idx++] = (a << 2) | (b >> 4);
    if (i + 2 < s.length) out[idx++] = ((b & 0xf) << 4) | (c >> 2);
    if (i + 3 < s.length) out[idx++] = ((c & 0x3) << 6) | d;
  }
  return out.slice(0, idx);
}

// ─── Types ────────────────────────────────────────────────────────────────────

export interface AlgorandProviderConfig extends ChainProviderConfig {
  algodUrl: string;
  algodToken?: string;
  indexerUrl?: string;
  indexerToken?: string;
}

interface SuggestedParams {
  fee: number;
  firstRound: number;
  lastRound: number;
  genesisHash: string;
  genesisId: string;
  flatFee: boolean;
}

/**
 * Result of signAndEncodeTransaction — carries both the raw Ed25519 signature
 * and, if native encoding is available, the msgpack-encoded signed transaction
 * ready to POST to algod.
 */
export interface AlgorandSignedTransaction {
  /** Raw Ed25519 signature (64 bytes). */
  signature: Uint8Array;
  /** Signer public key (32 bytes). */
  publicKey: Uint8Array;
  /**
   * Msgpack-encoded signed transaction suitable for direct algod submission.
   * Present only when native encoding succeeded (requires algosdk or a
   * bundled msgpack implementation — see encodeAlgorandTransaction()).
   */
  encoded?: Uint8Array;
  /** The original unsigned transaction descriptor bytes (always present). */
  descriptorBytes: Uint8Array;
}

// ─── Provider ─────────────────────────────────────────────────────────────────

export class AlgorandProvider implements ChainProvider {
  readonly chainId = "algorand";
  readonly displayName = "Algorand";
  readonly supportsDex = true;
  readonly supportsBridging = true;

  private readonly config: AlgorandProviderConfig;

  constructor(config: AlgorandProviderConfig) {
    this.config = config;
  }

  // ─── Queries ───────────────────────────────────────────────────────────────

  async getBalance(address: string, tokenId?: string): Promise<Result<BalanceInfo, SdkError>> {
    try {
      const resp = await this.algodFetch(`/v2/accounts/${address}`);
      const account = (await resp.json()) as { amount: number; assets?: Array<{ "asset-id": number; amount: number }> };

      if (!tokenId) {
        const microAlgos = account.amount;
        return ok({
          amount: (microAlgos / 1_000_000).toFixed(6),
          decimals: 6,
          symbol: "ALGO",
          raw: account,
        });
      }

      const asset = account.assets?.find((a) => a["asset-id"] === Number(tokenId));

      return ok({
        amount: (asset?.amount ?? 0).toString(),
        decimals: 0,
        symbol: `ASA#${tokenId}`,
        raw: asset,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Balance fetch failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  async validateAddress(address: string): Promise<Result<boolean, SdkError>> {
    if (address.length !== 58) {
      return ok(false);
    }
    const valid = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    for (const c of address) {
      if (!valid.includes(c)) return ok(false);
    }
    return ok(true);
  }

  async getAssets(address: string): Promise<Result<AssetInfo[], SdkError>> {
    try {
      if (!this.config.indexerUrl) {
        return err(new SdkError(SdkErrorCode.UNSUPPORTED_OPERATION, "Indexer not configured", { chain: "algorand" }));
      }

      const resp = await this.indexerFetch(`/v2/accounts/${address}/assets`);
      const data = (await resp.json()) as { assets?: Array<{ "asset-id": number; amount: number; "is-frozen": boolean }> };
      const assets: AssetInfo[] = (data.assets ?? []).map((a) => ({
        id: a["asset-id"].toString(),
        name: `ASA#${a["asset-id"]}`,
        symbol: `ASA#${a["asset-id"]}`,
        amount: a.amount.toString(),
        decimals: 0,
        raw: a,
      }));

      return ok(assets);
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Assets fetch failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  async getTransactionStatus(txHash: string): Promise<Result<TransactionResult, SdkError>> {
    try {
      const resp = await this.algodFetch(`/v2/transactions/pending/${txHash}`);
      const data = (await resp.json()) as { "confirmed-round": number };

      return ok({
        txHash,
        chain: "algorand",
        status: data["confirmed-round"] > 0 ? "confirmed" : "submitted",
        raw: data,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Status fetch failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  async getTokenMetadata(tokenId: string): Promise<Result<Record<string, unknown>, SdkError>> {
    try {
      if (!this.config.indexerUrl) {
        return err(new SdkError(SdkErrorCode.UNSUPPORTED_OPERATION, "Indexer not configured", { chain: "algorand" }));
      }

      const resp = await this.indexerFetch(`/v2/assets/${tokenId}`);
      const data = (await resp.json()) as { asset?: { params?: { name?: string; "unit-name"?: string; total?: number; decimals?: number; creator?: string; url?: string } } };
      const params = data.asset?.params;

      return ok({
        chain: "algorand",
        assetId: tokenId,
        name: params?.name ?? "Unknown",
        unitName: params?.["unit-name"] ?? "",
        totalSupply: params?.total?.toString() ?? "0",
        decimals: params?.decimals ?? 0,
        creator: params?.creator ?? "",
        url: params?.url ?? "",
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Metadata fetch failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  async getChainInfo(): Promise<Result<Record<string, unknown>, SdkError>> {
    try {
      const resp = await this.algodFetch("/v2/status");
      const status = (await resp.json()) as { "last-round": number; "last-version": string };

      return ok({
        chain: "algorand",
        network: this.config.network,
        lastRound: status["last-round"].toString(),
        lastVersion: status["last-version"],
        rpcUrl: this.config.algodUrl,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Chain info failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  // ─── Transaction Building ──────────────────────────────────────────────────

  async buildTransfer(params: TransferParams): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const sp = await this.getSuggestedParams();

      if (params.tokenId) {
        return ok(this.buildAsaTransferTx(params, sp));
      }
      return ok(this.buildPaymentTx(params, sp));
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Build transfer failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  async buildMint(params: MintParams): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const sp = await this.getSuggestedParams();

      // ASA creation transaction
      // Type: "acfg" (Asset Config) with no asset-id = create
      const txObj = {
        type: "acfg",
        from: params.creator,
        fee: sp.fee,
        firstRound: sp.firstRound,
        lastRound: sp.lastRound,
        genesisHash: sp.genesisHash,
        genesisId: sp.genesisId,
        assetTotal: params.totalSupply,
        assetDecimals: params.decimals,
        assetUnitName: params.symbol.slice(0, 8),
        assetName: params.name,
        assetURL: params.metadataUri ?? "",
        assetManager: params.creator,
        assetReserve: params.creator,
        assetFreeze: params.creator,
        assetClawback: params.creator,
      };

      return ok({
        chain: "algorand",
        format: "json-descriptor" as const,
        bytes: new TextEncoder().encode(JSON.stringify(txObj)),
        description: `Create ASA: ${params.name} (${params.symbol})`,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Build mint failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  async buildBurn(params: BurnParams): Promise<Result<UnsignedTransaction, SdkError>> {
    try {
      const sp = await this.getSuggestedParams();

      // ASA close-out: transfers entire holding back to self and removes the
      // asset from the account. On Algorand, closeRemainderTo closes the FULL
      // holding regardless of the `amount` field. True supply-reduction burning
      // requires the asset manager's private key (server-side operation).
      const txObj = {
        type: "axfer",
        from: params.owner,
        to: params.owner,
        assetIndex: Number(params.tokenId),
        amount: 0, // closeRemainderTo handles the full balance
        closeRemainderTo: params.owner,
        fee: sp.fee,
        firstRound: sp.firstRound,
        lastRound: sp.lastRound,
        genesisHash: sp.genesisHash,
        genesisId: sp.genesisId,
      };

      return ok({
        chain: "algorand",
        format: "json-descriptor" as const,
        bytes: new TextEncoder().encode(JSON.stringify(txObj)),
        description: `Close out entire holding of ASA ${params.tokenId} (Algorand does not support partial burns client-side)`,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Build burn failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  // ─── Signing + Submission ──────────────────────────────────────────────────

  /**
   * Signs the transaction using the provided Signer.
   *
   * For json-descriptor format transactions the signer.sign() is called with
   * the raw descriptor bytes. Wallet adapters that understand the descriptor
   * format will convert to native Algorand bytes internally before signing.
   * This is by design — the SDK builds portable descriptors; adapters own the
   * final encoding step.
   *
   * Returns the raw bytes produced by the signer (typically the signed
   * msgpack-encoded transaction ready for algod submission).
   */
  async signTransaction(tx: UnsignedTransaction, signer: Signer): Promise<Result<Uint8Array, SdkError>> {
    try {
      const signed = await signer.sign(tx.bytes);
      return ok(signed);
    } catch (e) {
      return err(new SdkError(SdkErrorCode.SIGNING_ERROR, `Signing failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  /**
   * Signs a transaction with full Ed25519 awareness using @noble/curves.
   *
   * Unlike signTransaction() (which delegates entirely to the Signer adapter),
   * this method:
   *   1. Calls encodeAlgorandTransaction() to build the canonical signing bytes
   *      (the "TX" prefix + msgpack payload that Algorand nodes verify).
   *   2. Uses @noble/curves ed25519.sign() directly when available, falling
   *      back to signer.sign() on the descriptor bytes when the lib is absent.
   *   3. Returns an AlgorandSignedTransaction with the raw signature,
   *      public key, original descriptor, and — if native encoding is
   *      available — the fully-encoded signed transaction.
   *
   * Requires @noble/curves as an optional peer dependency.
   * When not installed, falls back to signer.sign() on the descriptor bytes.
   *
   * @param tx - Unsigned transaction (json-descriptor format)
   * @param signer - Signer with publicKey and sign()
   * @param privateKey - Optional raw 32-byte Ed25519 private key for direct
   *   @noble/curves signing. When omitted signer.sign() is used.
   */
  async signAndEncodeTransaction(
    tx: UnsignedTransaction,
    signer: Signer,
    privateKey?: Uint8Array,
  ): Promise<Result<AlgorandSignedTransaction, SdkError>> {
    try {
      const ed25519 = await getEd25519();

      let signature: Uint8Array;
      let encoded: Uint8Array | undefined;

      if (ed25519 && privateKey) {
        // Ed25519 direct signing requires native-format transactions (msgpack-encoded).
        // json-descriptor format cannot produce valid Algorand signing bytes because
        // algod verifies signatures against canonical msgpack, not JSON.
        if (tx.format === "json-descriptor") {
          return err(new SdkError(
            SdkErrorCode.UNSUPPORTED_OPERATION,
            "signAndEncodeTransaction with Ed25519 requires native-format transactions. " +
            "json-descriptor format cannot produce valid Algorand signing bytes without " +
            "a msgpack encoder. Use signTransaction() with a wallet adapter instead, or " +
            "install algosdk to produce native transactions.",
            { chain: "algorand" }
          ));
        }

        // Native format: prefix with "TX" and sign
        const signingBytes = this.encodeAlgorandTransaction(tx);
        signature = ed25519.sign(signingBytes, privateKey);
        encoded = undefined; // Full msgpack envelope needs algosdk.encodeObj()
      } else {
        // Fallback: delegate entirely to the signer adapter. The adapter is
        // responsible for encoding and signing (e.g., a MyAlgo or Pera adapter
        // will receive the descriptor and produce algod-ready bytes).
        signature = await signer.sign(tx.bytes);
      }

      return ok({
        signature,
        publicKey: signer.publicKey,
        encoded,
        descriptorBytes: tx.bytes,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.SIGNING_ERROR, `signAndEncodeTransaction failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  /**
   * Submits a signed transaction to the algod node.
   *
   * Accepts either:
   *   - Raw signed bytes (Uint8Array) from signTransaction()
   *   - The `encoded` field from AlgorandSignedTransaction when native
   *     encoding was available
   */
  async submitTransaction(signedTx: Uint8Array): Promise<Result<TransactionResult, SdkError>> {
    try {
      const resp = await fetch(`${this.config.algodUrl}/v2/transactions`, {
        method: "POST",
        headers: {
          "Content-Type": "application/x-binary",
          ...(this.config.algodToken ? { "X-Algo-API-Token": this.config.algodToken } : {}),
        },
        body: signedTx,
      });

      if (!resp.ok) {
        const text = await resp.text().catch(() => `HTTP ${resp.status}`);
        return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `algod rejected transaction: ${text}`, { chain: "algorand" }));
      }

      const data = (await resp.json()) as { txId: string };
      return ok({
        txHash: data.txId,
        chain: "algorand",
        status: "submitted",
        raw: data,
      });
    } catch (e) {
      return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Submit failed: ${e}`, { chain: "algorand", cause: e as Error }));
    }
  }

  // ─── Encoding ─────────────────────────────────────────────────────────────

  /**
   * Builds the canonical Algorand signing bytes for a json-descriptor
   * transaction.
   *
   * Algorand's signing domain is:
   *   bytes_to_sign = b"TX" + msgpack_encode(transaction_object)
   *
   * This implementation extracts the JSON descriptor from `tx.bytes` and
   * prepends the "TX" domain prefix so it can be passed directly to an
   * Ed25519 signing function.
   *
   * IMPORTANT: The output is NOT a valid msgpack-encoded transaction object.
   * It is: [0x54, 0x58, ...json_bytes]. This is sufficient for signing when
   * the receiving node/adapter understands the descriptor convention. For a
   * fully spec-compliant encoding suitable for direct algod submission you
   * must msgpack-encode the transaction fields (not the JSON string) and
   * prefix with b"TX". Use algosdk or a msgpack library for that path.
   *
   * Future enhancement: when a msgpack library is available, replace the
   * JSON passthrough with proper field-level msgpack encoding:
   *   const txFields = JSON.parse(new TextDecoder().decode(tx.bytes));
   *   const encoded = msgpack.encode(txFields); // canonical key ordering required
   *   return new Uint8Array([0x54, 0x58, ...encoded]);
   */
  encodeAlgorandTransaction(tx: UnsignedTransaction): Uint8Array {
    // "TX" prefix as bytes [0x54, 0x58]
    const prefix = new Uint8Array([0x54, 0x58]);
    const combined = new Uint8Array(prefix.length + tx.bytes.length);
    combined.set(prefix, 0);
    combined.set(tx.bytes, prefix.length);
    return combined;
  }

  /**
   * Encodes bytes as a base64 string using a cross-platform approach that
   * avoids direct btoa() on binary data (safe only for latin-1 range).
   */
  bytesToBase64(bytes: Uint8Array): string {
    return bytesToBase64(bytes);
  }

  /**
   * Decodes a base64 string to bytes using a cross-platform approach.
   */
  base64ToBytes(b64: string): Uint8Array {
    return base64ToBytes(b64);
  }

  // ─── Internal helpers ──────────────────────────────────────────────────────

  private buildPaymentTx(params: TransferParams, sp: SuggestedParams): UnsignedTransaction {
    const txObj = {
      type: "pay",
      from: params.from,
      to: params.to,
      amount: Math.round(Number(params.amount) * 1_000_000), // ALGO → microAlgos
      fee: sp.fee,
      firstRound: sp.firstRound,
      lastRound: sp.lastRound,
      genesisHash: sp.genesisHash,
      genesisId: sp.genesisId,
      note: params.memo ? new TextEncoder().encode(params.memo) : undefined,
    };

    return {
      chain: "algorand",
      format: "json-descriptor" as const,
      bytes: new TextEncoder().encode(JSON.stringify(txObj)),
      description: `Send ${params.amount} ALGO to ${params.to}`,
    };
  }

  private buildAsaTransferTx(params: TransferParams, sp: SuggestedParams): UnsignedTransaction {
    const txObj = {
      type: "axfer",
      from: params.from,
      to: params.to,
      assetIndex: Number(params.tokenId),
      amount: Number(params.amount),
      fee: sp.fee,
      firstRound: sp.firstRound,
      lastRound: sp.lastRound,
      genesisHash: sp.genesisHash,
      genesisId: sp.genesisId,
    };

    return {
      chain: "algorand",
      format: "json-descriptor" as const,
      bytes: new TextEncoder().encode(JSON.stringify(txObj)),
      description: `Transfer ${params.amount} of ASA ${params.tokenId} to ${params.to}`,
    };
  }

  private async getSuggestedParams(): Promise<SuggestedParams> {
    const resp = await this.algodFetch("/v2/transactions/params");
    const data = (await resp.json()) as { "min-fee": number; "last-round": number; "genesis-hash": string; "genesis-id": string };
    return {
      fee: Math.max(data["min-fee"] ?? 1000, 1000),
      firstRound: data["last-round"],
      lastRound: data["last-round"] + 1000,
      genesisHash: data["genesis-hash"],
      genesisId: data["genesis-id"],
      flatFee: true,
    };
  }

  private async algodFetch(path: string, init?: RequestInit): Promise<Response> {
    const headers: Record<string, string> = {};
    if (this.config.algodToken) headers["X-Algo-API-Token"] = this.config.algodToken;
    return fetch(`${this.config.algodUrl}${path}`, { ...init, headers: { ...headers, ...init?.headers } });
  }

  private async indexerFetch(path: string): Promise<Response> {
    if (!this.config.indexerUrl) throw new Error("Indexer not configured");
    const headers: Record<string, string> = {};
    if (this.config.indexerToken) headers["X-Indexer-API-Token"] = this.config.indexerToken;
    return fetch(`${this.config.indexerUrl}${path}`, { headers });
  }
}
