# Avatar NFT Service Documentation

## Overview

The Avatar NFT Service provides a comprehensive solution for tightly coupling avatars with wallets, contracts, and holons through NFTs on blockchain networks. This service enables:

- **Avatar NFT Minting**: Create unique NFTs that represent avatars on blockchain networks
- **Holon Integration**: Bind holons to avatar NFTs with specific roles and permissions
- **Wallet Integration**: Connect wallets to avatar NFTs with controlled access levels
- **NFT Transfer**: Transfer avatar NFTs between addresses with proper validation
- **Access Control**: Verify ownership and permissions for holon and wallet access
- **Composite Views**: Get comprehensive views of avatar NFTs and their bindings

## Architecture

### Core Components

1. **AvatarNFT**: Represents an avatar as an NFT on blockchain
2. **HolonNFTBinding**: Links holons to avatar NFTs with permissions
3. **WalletNFTBinding**: Connects wallets to avatar NFTs with access control
4. **AvatarNFTService**: Main service orchestrating all operations
5. **AvatarNFTController**: REST API endpoints for the service

### Database Schema

#### AvatarNFT Table
- `Id`: Primary key
- `AvatarId`: Reference to the avatar
- `NFTContractAddress`: Blockchain contract address
- `TokenId`: NFT token identifier
- `ChainType`: Blockchain network (Solana, Algorand, etc.)
- `TokenStandard`: ERC721, ERC1155, etc.
- `MetadataURI`: Link to NFT metadata
- `Attributes`: Custom attributes as JSON
- `IsSoulbound`: Non-transferable flag
- `CurrentOwner`: Current blockchain address
- `IsActive`: Active status

#### HolonNFTBinding Table
- `Id`: Primary key
- `HolonId`: Reference to holon
- `AvatarNFTId`: Reference to avatar NFT
- `Role`: Owner, operator, delegate, etc.
- `PermissionLevel`: Full, limited, read-only
- `Permissions`: Fine-grained permissions as JSON

#### WalletNFTBinding Table
- `Id`: Primary key
- `WalletId`: Reference to wallet
- `AvatarNFTId`: Reference to avatar NFT
- `BindingType`: Primary, secondary, authorized
- `AccessLevel`: Full, transaction, view
- `AccessPermissions`: Specific access permissions as JSON

## API Endpoints

### Avatar NFT Management

#### POST /api/AvatarNFT/mint
Mint a new avatar NFT
```json
{
  "chainType": "Solana",
  "nftContractAddress": "11111111111111111111111111111111",
  "tokenStandard": "ERC721",
  "metadataURI": "https://api.example.com/metadata/123",
  "name": "My Avatar",
  "description": "A unique avatar representation",
  "attributes": {
    "level": 1,
    "karma": 100
  },
  "royaltyPercentage": 10.0,
  "royaltyRecipient": "recipient_address",
  "isSoulbound": false,
  "isTransferable": true
}
```

#### GET /api/AvatarNFT/{id}
Get avatar NFT by ID

#### GET /api/AvatarNFT/by-token/{chainType}/{contractAddress}/{tokenId}
Get avatar NFT by blockchain identifiers

#### GET /api/AvatarNFT/avatar/{avatarId}
Get all NFTs for an avatar

#### POST /api/AvatarNFT/{id}/transfer
Transfer avatar NFT to new address
```json
{
  "recipientAddress": "new_owner_address"
}
```

#### DELETE /api/AvatarNFT/{id}
Burn (delete) avatar NFT

### Holon Binding Management

#### POST /api/AvatarNFT/{avatarNFTId}/holons/{holonId}/bind
Bind holon to avatar NFT
```json
{
  "role": "owner",
  "permissionLevel": "full",
  "permissions": {
    "read": true,
    "write": true,
    "execute": true
  }
}
```

#### GET /api/AvatarNFT/{avatarNFTId}/holons
Get all holon bindings for avatar NFT

#### PUT /api/AvatarNFT/holons/{bindingId}
Update holon binding
```json
{
  "role": "operator",
  "permissionLevel": "limited",
  "permissions": {
    "read": true,
    "write": false
  }
}
```

#### DELETE /api/AvatarNFT/holons/{bindingId}
Remove holon binding

### Wallet Binding Management

#### POST /api/AvatarNFT/{avatarNFTId}/wallets/{walletId}/bind
Bind wallet to avatar NFT
```json
{
  "bindingType": "primary",
  "accessLevel": "full",
  "accessPermissions": {
    "sign": true,
    "view": true,
    "transfer": true
  }
}
```

#### GET /api/AvatarNFT/{avatarNFTId}/wallets
Get all wallet bindings for avatar NFT

#### PUT /api/AvatarNFT/wallets/{bindingId}
Update wallet binding
```json
{
  "bindingType": "secondary",
  "accessLevel": "transaction",
  "accessPermissions": {
    "sign": true,
    "view": false
  }
}
```

#### DELETE /api/AvatarNFT/wallets/{bindingId}
Remove wallet binding

### Composite Views

#### GET /api/AvatarNFT/{avatarNFTId}/composite
Get comprehensive view of avatar NFT with all bindings

#### GET /api/AvatarNFT/avatar/{avatarId}/composite
Get composite views of all NFTs for an avatar

### Verification

#### POST /api/AvatarNFT/verify-ownership
Verify avatar NFT ownership
```json
{
  "chainType": "Solana",
  "nftContractAddress": "11111111111111111111111111111111",
  "tokenId": "123"
}
```

#### POST /api/AvatarNFT/verify-holon-access
Verify holon access through avatar NFT
```json
{
  "avatarNFTId": "guid",
  "holonId": "guid",
  "requiredPermission": "read"
}
```

#### POST /api/AvatarNFT/verify-wallet-access
Verify wallet access through avatar NFT
```json
{
  "avatarNFTId": "guid",
  "walletId": "guid",
  "requiredAccess": "sign"
}
```

## Key Features

### 1. Tight Coupling
- Avatar NFTs serve as the central identity anchor
- Holons and wallets are bound to avatar NFTs with specific permissions
- All relationships are stored on-chain and off-chain for consistency

### 2. Permission System
- **Role-based access**: Owner, operator, delegate, etc.
- **Fine-grained permissions**: Individual permission flags for specific actions
- **Access levels**: High-level access categories (full, limited, read-only)

### 3. NFT Properties
- **Soulbound NFTs**: Non-transferable avatar representations
- **Transferable NFTs**: Standard NFTs that can be transferred
- **Metadata integration**: Rich metadata with custom attributes
- **Royalty support**: Built-in royalty distribution

### 4. Security
- **Ownership verification**: On-chain ownership validation
- **Access control**: Permission-based access to holons and wallets
- **Transaction signing**: Secure transaction signing through wallet bindings

### 5. Integration
- **Multi-chain support**: Works with various blockchain networks
- **Wallet integration**: Seamless connection to blockchain wallets
- **Holon ecosystem**: Deep integration with the holon system

## Usage Examples

### Example 1: Minting an Avatar NFT with Holon Binding

```csharp
// Mint avatar NFT with holon binding
var mintModel = new AvatarNFTMintModel
{
    ChainType = "Solana",
    NFTContractAddress = "11111111111111111111111111111111",
    TokenStandard = "ERC721",
    MetadataURI = "https://api.example.com/metadata/123",
    Name = "My Avatar",
    Description = "Main avatar representation",
    Attributes = new Dictionary<string, string>
    {
        { "level", "1" },
        { "karma", "100" },
        { "class", "warrior" }
    },
    IsSoulbound = true,
    HolonBindings = new Dictionary<string, string>
    {
        { "holon-guid-1", "owner" },
        { "holon-guid-2", "operator" }
    }
};

var result = await _avatarNFTService.MintAvatarNFTAsync(avatarId, mintModel);
```

### Example 2: Composite View

```csharp
// Get comprehensive view of avatar NFT
var composite = await _avatarNFTService.GetAvatarNFTCompositeAsync(nftId);

// Access holon bindings
foreach (var holonBinding in composite.HolonBindings)
{
    Console.WriteLine($"Holon: {holonBinding.HolonId}, Role: {holonBinding.Role}");
    
    // Check permissions
    if (holonBinding.Permissions.ContainsKey("execute") && 
        bool.Parse(holonBinding.Permissions["execute"]))
    {
        // Can execute holon operations
    }
}

// Access wallet bindings
foreach (var walletBinding in composite.WalletBindings)
{
    Console.WriteLine($"Wallet: {walletBinding.WalletAddress}, Access: {walletBinding.AccessLevel}");
}
```

### Example 3: Access Verification

```csharp
// Verify holon access
var accessResult = await _avatarNFTService.VerifyHolonAccessAsync(
    avatarNFTId, 
    holonId, 
    "execute"
);

if (accessResult.Result)
{
    // Grant access to holon operations
}

// Verify wallet access
var walletAccessResult = await _avatarNFTService.VerifyWalletAccessAsync(
    avatarNFTId,
    walletId,
    "sign"
);

if (walletAccessResult.Result)
{
    // Allow wallet transaction signing
}
```

## Database Migration

The service includes a migration script to create the necessary database tables:

```bash
dotnet ef migrations add AddNFTSupport
dotnet ef database update
```

## Testing

Run the integration tests to verify the service functionality:

```bash
dotnet test OASIS.WebAPI.IntegrationTests/Controllers/AvatarNFTControllerIntegrationTests.cs
```

## Security Considerations

1. **Private Key Management**: Ensure secure storage of private keys for wallet operations
2. **Input Validation**: All inputs should be validated before processing
3. **Rate Limiting**: Implement rate limiting for NFT minting operations
4. **Audit Logging**: Log all NFT operations for security auditing
5. **Multi-factor Authentication**: Consider requiring MFA for sensitive operations

## Future Enhancements

1. **Multi-sig Support**: Support for multi-signature wallet operations
2. **NFT Staking**: Implement NFT staking mechanisms
3. **Cross-chain NFTs**: Support for cross-chain NFT transfers
4. **Dynamic Permissions**: Runtime permission updates
5. **NFT Marketplace**: Integration with NFT marketplaces
6. **Analytics**: NFT usage analytics and reporting

## Troubleshooting

### Common Issues

1. **NFT Minting Fails**: Check blockchain network connectivity and contract addresses
2. **Permission Denied**: Verify wallet bindings and access permissions
3. **Database Errors**: Ensure migration script was executed properly
4. **Sync Issues**: Check blockchain sync status for real-time updates

### Debug Mode

Enable debug logging for detailed troubleshooting:

```json
{
  "Logging": {
    "LogLevel": {
      "OASIS.WebAPI.Managers.AvatarNFTService": "Debug"
    }
  }
}
```