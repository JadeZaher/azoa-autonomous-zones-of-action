/**
 * Test data builders mirroring the .NET TestDataBuilders pattern.
 * Usage: new AlgorandConfigBuilder().onDevnet().build()
 */

import type { AlgorandProviderConfig } from "../../src/algorand/provider.js";
import type { SolanaProviderConfig } from "../../src/solana/provider.js";
import type { AzoaApiConfig } from "../../src/api/client.js";
import type { Signer, TransferParams, MintParams, BurnParams, SwapParams } from "../../src/core/types.js";

// ─── Config Builders ───

export class AlgorandConfigBuilder {
  private config: AlgorandProviderConfig = {
    rpcUrl: "https://testnet-algod.algonode.cloud",
    algodUrl: "https://testnet-algod.algonode.cloud",
    network: "testnet",
  };

  onDevnet() { this.config.network = "devnet"; return this; }
  onTestnet() { this.config.network = "testnet"; return this; }
  onMainnet() { this.config.network = "mainnet"; return this; }
  withAlgodUrl(url: string) { this.config.algodUrl = url; return this; }
  withToken(token: string) { this.config.algodToken = token; return this; }
  withIndexer(url: string, token?: string) {
    this.config.indexerUrl = url;
    this.config.indexerToken = token;
    return this;
  }

  build(): AlgorandProviderConfig { return { ...this.config }; }
}

export class SolanaConfigBuilder {
  private config: SolanaProviderConfig = {
    rpcUrl: "https://api.devnet.solana.com",
    network: "devnet",
  };

  onDevnet() { this.config.rpcUrl = "https://api.devnet.solana.com"; this.config.network = "devnet"; return this; }
  onTestnet() { this.config.rpcUrl = "https://api.testnet.solana.com"; this.config.network = "testnet"; return this; }
  onMainnet() { this.config.rpcUrl = "https://api.mainnet-beta.solana.com"; this.config.network = "mainnet"; return this; }
  withRpcUrl(url: string) { this.config.rpcUrl = url; return this; }

  build(): SolanaProviderConfig { return { ...this.config }; }
}

export class ApiConfigBuilder {
  private config: AzoaApiConfig = {
    baseUrl: "http://localhost:5000",
  };

  withBaseUrl(url: string) { this.config.baseUrl = url; return this; }
  withToken(token: string) { this.config.token = token; return this; }
  withRefreshCallback(fn: () => Promise<string>) { this.config.onTokenRefresh = fn; return this; }
  withTimeout(ms: number) { this.config.timeoutMs = ms; return this; }

  build(): AzoaApiConfig { return { ...this.config }; }
}

// ─── Param Builders ───

export class TransferParamsBuilder {
  private params: TransferParams = {
    from: "SENDER_ADDR_PLACEHOLDER_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
    to: "RECEIVER_ADDR_PLACEHOLDER_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
    amount: "1.0",
  };

  from(addr: string) { this.params.from = addr; return this; }
  to(addr: string) { this.params.to = addr; return this; }
  amount(amt: string) { this.params.amount = amt; return this; }
  token(id: string) { this.params.tokenId = id; return this; }
  memo(m: string) { this.params.memo = m; return this; }

  build(): TransferParams { return { ...this.params }; }
}

export class MintParamsBuilder {
  private params: MintParams = {
    name: "TestToken",
    symbol: "TST",
    totalSupply: "1000000",
    decimals: 6,
    creator: "CREATOR_ADDR_PLACEHOLDER_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
  };

  name(n: string) { this.params.name = n; return this; }
  symbol(s: string) { this.params.symbol = s; return this; }
  supply(s: string) { this.params.totalSupply = s; return this; }
  decimals(d: number) { this.params.decimals = d; return this; }
  creator(c: string) { this.params.creator = c; return this; }
  uri(u: string) { this.params.metadataUri = u; return this; }

  build(): MintParams { return { ...this.params }; }
}

export class BurnParamsBuilder {
  private params: BurnParams = {
    tokenId: "12345",
    amount: "100",
    owner: "OWNER_ADDR_PLACEHOLDER_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
  };

  token(id: string) { this.params.tokenId = id; return this; }
  amount(a: string) { this.params.amount = a; return this; }
  owner(o: string) { this.params.owner = o; return this; }

  build(): BurnParams { return { ...this.params }; }
}

export class SwapParamsBuilder {
  private params: SwapParams = {
    tokenIn: "0",
    tokenOut: "12345",
    amountIn: "1000000",
    slippageBps: 50,
    sender: "SENDER_ADDR_PLACEHOLDER_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
  };

  tokenIn(id: string) { this.params.tokenIn = id; return this; }
  tokenOut(id: string) { this.params.tokenOut = id; return this; }
  amount(a: string) { this.params.amountIn = a; return this; }
  slippage(bps: number) { this.params.slippageBps = bps; return this; }
  sender(s: string) { this.params.sender = s; return this; }

  build(): SwapParams { return { ...this.params }; }
}

// ─── Mock Signer ───

export function createMockSigner(publicKeyHex = "00".repeat(32)): Signer {
  const pubKey = new Uint8Array(32);
  for (let i = 0; i < 32; i++) {
    pubKey[i] = parseInt(publicKeyHex.slice(i * 2, i * 2 + 2), 16);
  }

  return {
    publicKey: pubKey,
    sign: async (message: Uint8Array) => {
      // Return a deterministic "signature" for testing (64 bytes)
      const sig = new Uint8Array(64);
      for (let i = 0; i < Math.min(message.length, 64); i++) {
        sig[i] = message[i]! ^ 0xff;
      }
      return sig;
    },
  };
}
