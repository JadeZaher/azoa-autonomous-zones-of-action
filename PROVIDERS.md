# OASIS Provider & API Architecture

This guide covers the full system architecture: adding new blockchain providers, the complete API surface, and how the SDK mirrors the backend.

## System Architecture

```
┌─────────────────────────────────────────────────────────┐
│  SDK (@oasis/wallet-sdk)                                │
│  ┌──────────────┐ ┌──────────────┐ ┌─────────────────┐ │
│  │ OasisClient   │ │ OasisWallet  │ │ OasisApiClient  │ │
│  │ (session,     │ │ (chain       │ │ (typed HTTP     │ │
│  │  holons,      │ │  providers,  │ │  client for     │ │
│  │  auth)        │ │  DEX, sign)  │ │  all endpoints) │ │
│  └──────────────┘ └──────────────┘ └─────────────────┘ │
│           │               │                │            │
│     AlgorandProvider  SolanaProvider   fetch()          │
│     TinymanAdapter    JupiterAdapter                    │
└────────────┼──────────────┼────────────────┼────────────┘
             │              │                │
      ┌──────▼──────────────▼──────┐  ┌──────▼──────────┐
      │  Blockchain Nodes          │  │  .NET WebAPI     │
      │  (Algod, Solana RPC)       │  │  (REST)          │
      └────────────────────────────┘  └──────────────────┘
                                              │
                                      ┌───────▼───────┐
                                      │  9 Controllers │
                                      │  7 Managers    │
                                      │  ProviderCtx   │
                                      └───────┬───────┘
                                              │
                          ┌───────────────────┼───────────────────┐
                          │                   │                   │
                   IOASISStorage    IBlockchainProvider   ICrossChainBridge
                   Provider         Factory              Service
                   (EF/InMemory)    (Algo/Sol)           (Trusted/Wormhole)
```

## Adding a New Blockchain Provider

Each chain has two mirrored implementations:
1. **Backend (.NET)** — `Providers/Blockchain/<Chain>/<Chain>Provider.cs`
2. **SDK (TypeScript)** — `sdk/oasis-wallet/src/<chain>/provider.ts`

### Step 1: Backend (.NET)

Create `Providers/Blockchain/<Chain>/<Chain>Provider.cs`:

```csharp
public class <Chain>Provider : BaseBlockchainProvider
{
    public override string ChainType => "<chain>";
    public override bool SupportsBridging => true;

    // REQUIRED (9 methods):
    GetBalanceAsync, ValidateAddressAsync, MintAsync, BurnAsync, TransferAsync,
    GetTokenMetadataAsync, GetTokensByOwnerAsync, GetTransactionStatusAsync, GetChainInfoAsync

    // OPTIONAL — bridge (4 methods):
    LockForBridgeAsync, MintWrappedAsync, BurnWrappedAsync, VerifyBridgeProofAsync

    // OPTIONAL — DEX (2 methods):
    ExchangeAsync, SwapAsync
}
```

Register: `builder.Services.AddSingleton<IBlockchainProvider, <Chain>Provider>();` in Program.cs

### Step 2: SDK (TypeScript)

Create `sdk/oasis-wallet/src/<chain>/provider.ts` implementing `ChainProvider`:

```typescript
export class <Chain>Provider implements ChainProvider {
  readonly chainId = "<chain>";
  // 12 methods: getBalance, validateAddress, getAssets, getTransactionStatus,
  //             getTokenMetadata, getChainInfo, buildTransfer, buildMint,
  //             buildBurn, signTransaction, submitTransaction
}
```

Add tsup entry + package.json export for `"./<chain>"`.

### Step 3: Optional DEX Adapter

```typescript
export class <DexName>Adapter implements DexAdapter {
  readonly chainId = "<chain>";
  getQuote(params) → SwapQuote
  buildSwapTransaction(quote, sender) → UnsignedTransaction
}
```

### Interface Mirror

| .NET (IBlockchainProvider)       | SDK (ChainProvider)         | Notes |
|----------------------------------|-----------------------------|-------|
| `GetBalanceAsync`                | `getBalance`                | Both live |
| `ValidateAddressAsync`           | `validateAddress`           | Both live |
| `MintAsync`                      | `buildMint`                 | .NET server-side; SDK returns unsigned tx |
| `BurnAsync`                      | `buildBurn`                 | Same pattern |
| `TransferAsync`                  | `buildTransfer`             | Same pattern |
| `GetTokenMetadataAsync`          | `getTokenMetadata`          | Both live |
| `GetTokensByOwnerAsync`          | `getAssets`                 | Both live |
| `GetTransactionStatusAsync`      | `getTransactionStatus`      | Both live |
| `GetChainInfoAsync`              | `getChainInfo`              | Both live |
| `ExchangeAsync` / `SwapAsync`    | `DexAdapter.getQuote/build` | SDK uses DEX adapters |
| `LockForBridgeAsync`             | via `OasisApiClient.bridge` | Bridge is server-orchestrated |
| `DeployContractAsync`            | (chain-specific methods)    | Not in base SDK interface |

### Existing Providers

| Chain     | Backend                          | SDK                                | DEX       | Bridge |
|-----------|----------------------------------|------------------------------------|-----------|--------|
| Algorand  | `Providers/Blockchain/Algorand/` | `sdk/oasis-wallet/src/algorand/`   | Tinyman   | Yes    |
| Solana    | `Providers/Blockchain/Solana/`   | `sdk/oasis-wallet/src/solana/`     | Jupiter   | Yes    |

---

## Complete API Surface

### Avatar (Identity & Auth)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/avatar/register` | POST | No | Create account (returns IAvatar) |
| `api/avatar/login` | POST | No | Authenticate (returns JWT) |
| `api/avatar/{id}` | GET | Yes | Get avatar profile |
| `api/avatar` | GET | Yes | List all avatars |
| `api/avatar/{id}` | PUT | Yes | Update profile |
| `api/avatar/{id}` | DELETE | Yes | Delete account |

### Holon (Universal Data Node)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/holon/{id}` | GET | Yes | Get holon |
| `api/holon` | GET | Yes | Query with filters |
| `api/holon` | POST | Yes | Create holon |
| `api/holon/{id}` | PUT | Yes | Update holon |
| `api/holon/{id}` | DELETE | Yes | Delete holon |
| `api/holon/{id}/children` | GET | Yes | Get child holons |
| `api/holon/{id}/peers` | GET | Yes | Get peer holons |
| `api/holon/{id}/ancestors` | GET | Yes | Walk up the tree |
| `api/holon/{id}/descendants` | GET | Yes | Walk down the tree |
| `api/holon/{id}/mint` | POST | Yes | Mint holon on-chain |
| `api/holon/{id}/exchange` | POST | Yes | Exchange holon token |
| `api/holon/{id}/propagate` | POST | Yes | Propagate changes to subtree |
| `api/holon/{id}/compose` | GET | Yes | Composite view |
| `api/holon/{id}/clone` | POST | Yes | Clone holon/subtree |
| `api/holon/{id}/move` | POST | Yes | Move subtree to new parent |

### Wallet

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/wallet/{id}` | GET | Yes | Get wallet |
| `api/wallet` | GET | Yes | Query wallets |
| `api/wallet` | POST | Yes | Create wallet |
| `api/wallet/{id}` | PUT | Yes | Update wallet |
| `api/wallet/{id}` | DELETE | Yes | Delete wallet |
| `api/wallet/{id}/set-default` | POST | Yes | Set as default for chain |
| `api/wallet/{id}/portfolio` | GET | Yes | Portfolio with live balance + NFTs |

### NFT (Holons with AssetType="NFT")

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/nft/{id}` | GET | Yes | Get NFT |
| `api/nft` | GET | Yes | Query NFTs |
| `api/nft/mint` | POST | Yes | Mint NFT (creates holon + blockchain op) |
| `api/nft/{id}/transfer` | POST | Yes | Transfer NFT ownership |
| `api/nft/{id}/burn` | POST | Yes | Burn NFT |
| `api/nft/{id}/metadata` | GET | No | Get NFT metadata (public) |

### AvatarNFT (On-Chain Identity Bindings)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/avatarnft/mint` | POST | Yes | Mint avatar NFT |
| `api/avatarnft/{id}` | GET | Yes | Get avatar NFT |
| `api/avatarnft/by-token/{chain}/{contract}/{tokenId}` | GET | Yes | Lookup by on-chain token |
| `api/avatarnft/avatar/{avatarId}` | GET | Yes | Get all NFTs for avatar |
| `api/avatarnft/{id}/transfer` | POST | Yes | Transfer |
| `api/avatarnft/{id}` | DELETE | Yes | Burn |
| `api/avatarnft/{nftId}/holons/{holonId}/bind` | POST | Yes | Bind holon to NFT |
| `api/avatarnft/{nftId}/holons` | GET | Yes | List holon bindings |
| `api/avatarnft/holons/{bindingId}` | PUT | Yes | Update binding |
| `api/avatarnft/holons/{bindingId}` | DELETE | Yes | Remove binding |
| `api/avatarnft/{nftId}/wallets/{walletId}/bind` | POST | Yes | Bind wallet to NFT |
| `api/avatarnft/{nftId}/wallets` | GET | Yes | List wallet bindings |
| `api/avatarnft/wallets/{bindingId}` | PUT | Yes | Update binding |
| `api/avatarnft/wallets/{bindingId}` | DELETE | Yes | Remove binding |
| `api/avatarnft/{nftId}/composite` | GET | Yes | Full composite view |
| `api/avatarnft/avatar/{avatarId}/composite` | GET | Yes | All composites for avatar |
| `api/avatarnft/verify-ownership` | POST | Yes | Verify on-chain ownership |
| `api/avatarnft/verify-holon-access` | POST | Yes | Check holon access via NFT |
| `api/avatarnft/verify-wallet-access` | POST | Yes | Check wallet access via NFT |

### Bridge (Cross-Chain)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/bridge/routes` | GET | Yes | Available bridge routes |
| `api/bridge/initiate` | POST | Yes | Start bridge (trusted or Wormhole) |
| `api/bridge/{id}` | GET | Yes | Bridge status |
| `api/bridge/{id}/fetch-vaa` | POST | Yes | Poll for Wormhole VAA |
| `api/bridge/{id}/redeem` | POST | Yes | Redeem on target chain |
| `api/bridge/{id}/complete` | POST | Yes | Mark trusted bridge complete |
| `api/bridge/{id}/reverse` | POST | Yes | Reverse a completed bridge |
| `api/bridge/history` | GET | Yes | Bridge history for avatar |

### Search

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/search` | POST | Yes | Cross-entity search |
| `api/search/facets` | GET | Yes | Available facets |

### Blockchain Operations (Read-Only History)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/blockchainoperation/{id}` | GET | Yes | Get operation |
| `api/blockchainoperation/avatar/{avatarId}` | GET | Yes | Operations by avatar |

### STAR ODK (dApp Generator)

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `api/starodk/{id}` | GET | Yes | Get ODK |
| `api/starodk` | GET | Yes | List all ODKs |
| `api/starodk` | POST | Yes | Create/update ODK |
| `api/starodk/{id}` | DELETE | Yes | Delete ODK |
| `api/starodk/{id}/generate` | POST | Yes | Generate dApp code |
| `api/starodk/{id}/deploy` | POST | Yes | Deploy dApp |

---

## Integration Patterns

### How Blockchain Connects to Entities

```
NftController.Mint → NftManager → creates Holon (AssetType="NFT") + BlockchainOperation
                                → BlockchainOperationManager.ExecuteAsync
                                → BlockchainProviderFactory.GetProvider(chainType)
                                → AlgorandProvider.MintAsync / SolanaProvider.MintAsync

HolonController.Mint → BlockchainOperationBuilder → BlockchainOperationManager
                                                  → same chain dispatch

BridgeController → CrossChainBridgeService → source.LockForBridgeAsync
                                           → target.MintWrappedAsync
                                           → (or Wormhole: initiate → fetchVAA → redeem)
```

### Two Provider Systems (Do Not Confuse)

1. **IOASISStorageProvider** — data persistence (EF/PostgreSQL or InMemory). Selected per-request via `OASISRequest` query parameter.
2. **IBlockchainProvider** — on-chain operations (Algorand, Solana). Selected by chain type via `BlockchainProviderFactory`.

### SDK ↔ Backend Data Flow

```
SDK OasisWallet                     .NET Backend
─────────────                       ────────────
wallet.buildTransfer(params)   →    (not called — SDK builds locally)
signer.sign(unsignedTx)       →    (not called — client-side)
wallet.submitTransaction(tx)  →    (direct to chain RPC, bypasses backend)

oasisApi.mintNft(params)      →    NftController.Mint → NftManager → storage
oasisApi.initiateBridge(...)  →    BridgeController → CrossChainBridgeService
oasisApi.search(query)        →    SearchController → SearchManager → storage
```
