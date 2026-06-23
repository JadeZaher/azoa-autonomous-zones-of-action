# Wallet API — Specification

## Goal
Promote wallet management from nested Avatar sub-routes to a first-class REST API with portfolio analytics and multi-chain support.

## Motivation
Currently wallets are accessed only via `/api/avatar/{id}/wallets`. This is limiting for:
- Wallet-first dApp integrations that need direct wallet CRUD
- Portfolio dashboards that aggregate across avatars
- Multi-chain wallet management independent of avatar context

## Architecture
Follows the established Controller → Manager → ProviderContext → IAZOAStorageProvider pattern.

### New / Modified Files
| Layer | File | Action |
|---|---|---|
| Interface | `Interfaces/Managers/IWalletManager.cs` | New |
| Manager | `Managers/WalletManager.cs` | New |
| Controller | `Controllers/WalletController.cs` | New |
| Models | `Models/Requests/WalletCreateModel.cs` | New |
| Models | `Models/Requests/WalletUpdateModel.cs` | New |
| Models | `Models/Responses/PortfolioResult.cs` | New |
| Interfaces | `Interfaces/Managers/IAvatarManager.cs` | Remove wallet methods (migrate to IWalletManager) |
| Manager | `Managers/AvatarManager.cs` | Remove wallet methods |
| Controller | `Controllers/AvatarController.cs` | Remove wallet endpoints (backward-compat optional) |

### IWalletManager Contract
```csharp
public interface IWalletManager
{
    Task<AZOAResult<IWallet>> GetAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IWallet>>> QueryAsync(WalletQueryRequest query, AZOARequest? request = null);
    Task<AZOAResult<IWallet>> CreateAsync(WalletCreateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IWallet>> UpdateAsync(Guid id, WalletUpdateModel model, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<bool>> SetDefaultAsync(Guid avatarId, Guid walletId, AZOARequest? request = null);
    Task<AZOAResult<PortfolioResult>> GetPortfolioAsync(Guid walletId, AZOARequest? request = null);
}
```

### WalletController Endpoints
| Method | Route | Auth | Description |
|---|---|---|---|
| GET | `/api/wallets/{id:guid}` | Authorize | Get wallet by ID |
| GET | `/api/wallets` | Authorize | Query wallets (by avatarId, chainType, isDefault) |
| POST | `/api/wallets` | Authorize | Create wallet for authenticated avatar |
| PUT | `/api/wallets/{id:guid}` | Authorize | Update wallet label, isDefault |
| DELETE | `/api/wallets/{id:guid}` | Authorize | Delete wallet |
| POST | `/api/wallets/{id:guid}/set-default` | Authorize | Set as default wallet for avatar |
| GET | `/api/wallets/{id:guid}/portfolio` | Authorize | Portfolio snapshot (stub → live provider later) |

### Models
**WalletCreateModel**
```csharp
public class WalletCreateModel
{
    public string ChainType { get; set; } = string.Empty;   // e.g. "Solana", "Algorand", "Ethereum"
    public string Address { get; set; } = string.Empty;
    public string? PublicKey { get; set; }
    public string? Label { get; set; }
    public bool IsDefault { get; set; }
}
```

**WalletUpdateModel**
```csharp
public class WalletUpdateModel
{
    public string? Label { get; set; }
    public bool? IsDefault { get; set; }
}
```

**WalletQueryRequest**
```csharp
public class WalletQueryRequest
{
    public Guid? AvatarId { get; set; }
    public string? ChainType { get; set; }
    public bool? IsDefault { get; set; }
}
```

**PortfolioResult**
```csharp
public class PortfolioResult
{
    public Guid WalletId { get; set; }
    public string ChainType { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Balance { get; set; }          // Stub: 0 until live blockchain integration
    public string Symbol { get; set; } = "SOL";   // Chain native token symbol
    public List<NftHoldings> Nfts { get; set; } = new(); // Linked Holons with AssetType="NFT"
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
}
```

## Business Rules
1. **Address uniqueness per chain**: A wallet address + chainType combination should be unique (enforced at manager level via provider query).
2. **Default wallet**: Only one default wallet per avatar per chainType. Setting a new default unsets the previous one.
3. **Portfolio stub**: `GetPortfolioAsync` returns a stub result with `Balance = 0` and linked NFT Holons. Future track will wire live `IBlockchainProvider.GetBalanceAsync`.
4. **Ownership enforcement**: Wallet create/update/delete must verify the authenticated avatar owns the wallet (same check currently in AvatarManager).

## Acceptance Criteria
- [ ] `IWalletManager` defined and implemented
- [ ] `WalletController` exposes all 7 endpoints
- [ ] `AvatarController` wallet endpoints removed or deprecated
- [ ] All endpoints respect `[Authorize]` and avatar ownership
- [ ] `SetDefaultAsync` correctly swaps default flags per chainType
- [ ] Portfolio endpoint returns linked NFT Holons for the wallet address
- [ ] Unit + integration tests cover CRUD, ownership, default swap, portfolio
- [ ] Stryker mutation score for new code ≥ 50 %
