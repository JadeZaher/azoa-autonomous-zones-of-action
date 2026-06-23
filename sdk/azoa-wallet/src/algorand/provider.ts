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
import {
  encodeAlgorandTransaction,
  buildSignedTransactionEnvelope,
} from "./msgpack.js";

// Algorand transaction signing has two supported paths:
//
//  1. Delegated path (signTransaction + signer adapter). The legacy path.
//     The SDK passes the json-descriptor bytes to the consumer's Signer
//     (typically Pera, Defly, MyAlgo, etc.). Adapters that understand the
//     descriptor convert it to canonical Algorand msgpack internally
//     before signing, then return the algod-submittable envelope. The
//     SDK never sees the private key. This path is appropriate for
//     wallet-adapter integrations that already speak the descriptor
//     format end-to-end.
//
//  2. Encode-then-sign path (signAndEncodeTransaction). The SDK performs
//     the canonical Algorand msgpack encoding itself, hands the
//     "TX"-prefixed bytes to the Signer, then assembles the algod-
//     submittable signed-transaction envelope from the returned
//     signature. Consumers wire their own ed25519 implementation into
//     the Signer (`@noble/curves`, a KMS, a hardware wallet exposed as
//     a Signer) — the SDK does not import any crypto library directly.
//     Use this path for generic Signers (server-side custody, raw
//     keypairs, tests) and any caller that needs `encoded` to be the
//     final submittable envelope rather than an opaque adapter response.

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

      if (!resp.ok) {
        const text = await resp.text().catch(() => `HTTP ${resp.status}`);
        return err(new SdkError(SdkErrorCode.NETWORK_ERROR, `Algorand node rejected request: ${text}`, { chain: "algorand" }));
      }

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
   * Encode-then-sign path: produces an algod-submittable signed-transaction
   * envelope directly from a `json-descriptor` UnsignedTransaction.
   *
   * Steps:
   *  1. Run the descriptor through canonical Algorand encoding
   *     ({@link encodeAlgorandTransaction}) to obtain the "TX"-prefixed
   *     bytes that ed25519 signs over.
   *  2. Pass those bytes to `signer.sign()`. The Signer is the only place
   *     a private key is referenced — the SDK does not hold raw keys.
   *  3. Assemble the canonical `{ sig, txn }` envelope via
   *     {@link buildSignedTransactionEnvelope} so the result is exactly
   *     what `POST /v2/transactions` expects.
   *
   * Returns `{ signature, encoded }` where `signature` is the bare 64-byte
   * ed25519 signature (useful for downstream verification) and `encoded`
   * is the submittable envelope. Unlike the legacy `signTransaction()`
   * path, `encoded` is guaranteed to be defined on the success arm — the
   * SDK owns the encoding step.
   *
   * Only `format: "json-descriptor"` is accepted; native / base64 inputs
   * are passed through `signTransaction()` instead because re-encoding
   * already-encoded bytes would be lossy.
   */
  async signAndEncodeTransaction(
    tx: UnsignedTransaction,
    signer: Signer
  ): Promise<Result<{ signature: Uint8Array; encoded: Uint8Array }, SdkError>> {
    try {
      if (tx.format !== "json-descriptor") {
        return err(
          new SdkError(
            SdkErrorCode.UNSUPPORTED_OPERATION,
            `signAndEncodeTransaction requires format='json-descriptor', got '${tx.format}'. Use signTransaction() for pre-encoded payloads.`,
            { chain: "algorand" }
          )
        );
      }

      const signingBytes = encodeAlgorandTransaction(tx);
      const signature = await signer.sign(signingBytes);

      if (signature.length !== 64) {
        return err(
          new SdkError(
            SdkErrorCode.SIGNING_ERROR,
            `Signer returned ${signature.length}-byte signature; expected 64 (ed25519).`,
            { chain: "algorand" }
          )
        );
      }

      const encoded = buildSignedTransactionEnvelope(tx, signature, signer.publicKey);
      return ok({ signature, encoded });
    } catch (e) {
      return err(
        new SdkError(SdkErrorCode.SIGNING_ERROR, `signAndEncodeTransaction failed: ${e}`, {
          chain: "algorand",
          cause: e as Error,
        })
      );
    }
  }

  /**
   * Submits a signed transaction to the algod node.
   *
   * Accepts the raw signed bytes produced by signTransaction() (which the
   * caller obtains by passing the unsigned descriptor to a wallet adapter
   * such as Pera or Defly — the adapter owns the msgpack encoding step).
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

  // ─── Encoding helpers ─────────────────────────────────────────────────────

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
