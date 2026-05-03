# NFT API — Specification

## Goal
Provide a semantic NFT layer on top of the existing Holon infrastructure, enabling mint, transfer, burn, and metadata operations without duplicating the Holon data model.

## Motivation
The OASIS reference ecosystem treats NFTs as first-class assets. Our `IHolon` already carries `AssetType`, `TokenId`, `ChainId`, `Metadata`, and `AvatarId` — the raw data shape of an NFT. However, consumers need NFT-specific semantics (ERC-721/ERC-1155 style metadata, ownership transfers, provenance) rather than generic Holon CRUD.

## Design Principle: **Composition over Duplication**
`INFT` is a **view interface** over `IHolon`. The `NftManager` internally delegates to `IHolonManager` and `IBlockchainOperationManager`, ensuring:
- No new storage schema or DbContext migrations
- NFTs are queryable as Holons via existing provider methods
- Blockchain operations are tracked via existing `BlockchainOperation` records

## Architecture
```
NftController → INftManager → IHolonManager (CRUD)
                          → IBlockchainOperationManager (on-chain ops)
                          → ProviderContext (provider switching)
```

### New / Modified Files
| Layer | File | Action |
|---|---|---|
| Interface | `Interfaces/INft.cs` | New (view over IHolon) |
| Interface | `Interfaces/Managers/INftManager.cs` | New |
| Manager | `Managers/NftManager.cs` | New |
| Controller | `Controllers/NftController.cs` | New |
| Models | `Models/Requests/NftMintRequest.cs` | New |
| Models | `Models/Requests/NftTransferRequest.cs` | New |
| Models | `Models/Requests/NftBurnRequest.cs` | New |
| Models | `Models/Responses/NftMetadata.cs` | New |
| Models | `Models/Responses/NftResult.cs` | New (wraps IHolon with NFT semantics) |

### INft Contract
```csharp
public interface INft : IHolon
{
    // All properties inherited from IHolon.
    // Extension methods or NftResult wrapper provide NFT-specific accessors:
    // - ImageUri  → Metadata["image"]
    // - ExternalUri → Metadata["external_url"]
    // - AnimationUri → Metadata["animation_url"]
    // - Attributes → Metadata["attributes"] (JSON serialized)
}
```

### INftManager Contract
```csharp
public interface INftManager
{
    Task<OASISResult<INft>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<INft>>> QueryAsync(NftQueryRequest query, OASISRequest? request = null);

    // Creates a Holon with AssetType = "NFT", triggers blockchain mint operation
    Task<OASISResult<IBlockchainOperation>> MintAsync(NftMintRequest request, Guid avatarId, OASISRequest? providerRequest = null);

    // Updates Holon AvatarId (ownership), triggers blockchain transfer operation
    Task<OASISResult<IBlockchainOperation>> TransferAsync(Guid nftId, NftTransferRequest request, Guid avatarId, OASISRequest? providerRequest = null);

    // Sets Holon IsActive = false, triggers blockchain burn operation
    Task<OASISResult<IBlockchainOperation>> BurnAsync(Guid nftId, Guid walletId, Guid avatarId, OASISRequest? providerRequest = null);

    // Returns standardized metadata object (ERC-721 compatible JSON shape)
    Task<OASISResult<NftMetadata>> GetMetadataAsync(Guid id, OASISRequest? request = null);
}
```

### NftController Endpoints
| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/nfts/{id:guid}` | Authorize | Get NFT by Holon ID |
| GET | `/api/nfts` | Authorize | Query NFTs (owner, chain, collection) |
| POST | `/api/nfts/mint` | Authorize | Mint new NFT (creates Holon + on-chain op) |
| POST | `/api/nfts/{id:guid}/transfer` | Authorize | Transfer ownership |
| POST | `/api/nfts/{id:guid}/burn` | Authorize | Burn NFT |
| GET | `/api/nfts/{id:guid}/metadata` | AllowAnonymous | ERC-721 compatible metadata JSON |

### Request Models
**NftMintRequest**
```csharp
public class NftMintRequest
{
    public Guid WalletId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChainId { get; set; } = string.Empty;
    public string? TokenId { get; set; }          // Optional; provider may assign
    public string? ImageUri { get; set; }
    public string? ExternalUri { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}
```

**NftTransferRequest**
```csharp
public class NftTransferRequest
{
    public Guid TargetAvatarId { get; set; }      // New owner
    public Guid WalletId { get; set; }            // Sender's wallet for on-chain tx
    public string? Memo { get; set; }
}
```

**NftBurnRequest**
```csharp
public class NftBurnRequest
{
    public Guid WalletId { get; set; }
}
```

**NftQueryRequest**
```csharp
public class NftQueryRequest
{
    public Guid? OwnerAvatarId { get; set; }
    public string? ChainId { get; set; }
    public string? TokenId { get; set; }
    public string? Name { get; set; }
}
```

### Response Models
**NftMetadata** (ERC-721 compatible)
```csharp
public class NftMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Image { get; set; }
    public string? ExternalUrl { get; set; }
    public List<NftAttribute> Attributes { get; set; } = new();
}

public class NftAttribute
{
    public string TraitType { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? DisplayType { get; set; }  // "number", "date", etc.
}
```

**NftResult** (wrapper for API responses)
```csharp
public class NftResult
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Guid? OwnerAvatarId { get; set; }
    public string ChainId { get; set; } = string.Empty;
    public string? TokenId { get; set; }
    public string AssetType { get; set; } = "NFT";
    public NftMetadata Metadata { get; set; } = new();
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public bool IsActive { get; set; }
}
```

## Business Rules
1. **AssetType enforcement**: All NFT operations filter/create Holons where `AssetType == "NFT"`. Query endpoints must ignore non-NFT Holons.
2. **Ownership verification**: Transfer and Burn require the authenticated avatar to match the Holon's current `AvatarId`.
3. **Metadata standard**: `GetMetadataAsync` maps Holon `Metadata` dictionary to `NftMetadata` with well-known keys (`image`, `external_url`, `animation_url`, `attributes`). Unknown keys remain accessible via raw metadata.
4. **Mint workflow**:
   - Create Holon with `AssetType = "NFT"`, `AvatarId = avatarId`, populate `Metadata` from request
   - Build `BlockchainOperation` via `BlockchainOperationBuilder` for `MintAsync`
   - Return operation result (not the Holon) so caller can poll status
5. **Transfer workflow**:
   - Verify ownership → Update Holon `AvatarId` to `TargetAvatarId` → Set `ModifiedDate`
   - Build `BlockchainOperation` for `TransferAsync`
   - Return operation result
6. **Burn workflow**:
   - Verify ownership → Set `IsActive = false` → Save Holon
   - Build `BlockchainOperation` for `BurnAsync`
   - Return operation result

## Acceptance Criteria
- [ ] `INftManager` defined and implemented
- [ ] `NftController` exposes all 6 endpoints
- [ ] `/api/nfts/{id}/metadata` returns ERC-721 compatible JSON
- [ ] Mint creates a Holon with `AssetType = "NFT"`
- [ ] Transfer updates Holon ownership and creates blockchain operation
- [ ] Burn deactivates Holon and creates blockchain operation
- [ ] Query filters only Holons where `AssetType == "NFT"`
- [ ] Ownership enforced on transfer/burn
- [ ] Unit + integration tests cover mint, transfer, burn, metadata, query
- [ ] Stryker mutation score for new code ≥ 50 %
