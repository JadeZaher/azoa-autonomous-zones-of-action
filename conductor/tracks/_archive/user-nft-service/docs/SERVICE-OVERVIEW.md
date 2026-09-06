# User NFT Service Documentation

## Overview

The User NFT Service provides a comprehensive solution for tightly coupling users with wallets, contracts, and holons through NFTs on blockchain networks. This service enables:

- **User NFT Minting**: Create unique NFTs that represent users on blockchain networks
- **Holon Integration**: Bind holons to user NFTs with specific roles and permissions
- **Wallet Integration**: Connect wallets to user NFTs with controlled access levels
- **NFT Transfer**: Transfer user NFTs between addresses with proper validation
- **Access Control**: Verify ownership and permissions for holon and wallet access
- **Composite Views**: Get comprehensive views of user NFTs and their bindings

## Architecture

### Core Components

1. **UserNFT**: Represents an user as an NFT on blockchain
2. **HolonNFTBinding**: Links holons to user NFTs with permissions
3. **WalletNFTBinding**: Connects wallets to user NFTs with access control
4. **UserNFTService**: Main service orchestrating all operations
5. **UserNFTController**: REST API endpoints for the service

### Database Schema

#### UserNFT Table
- `Id`: Primary key
- `UserId`: Reference to the user
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
- `UserNFTId`: Reference to user NFT
- `Role`: Owner, operator, delegate, etc.
- `PermissionLevel`: Full, limited, read-only
- `Permissions`: Fine-grained permissions as JSON

#### WalletNFTBinding Table
- `Id`: Primary key
- `WalletId`: Reference to wallet
- `UserNFTId`: Reference to user NFT
- `BindingType`: Primary, secondary, authorized
- `AccessLevel`: Full, transaction, view
- `AccessPermissions`: Specific access permissions as JSON

## API Endpoints

### User NFT Management

#### POST /api/UserNFT/mint
Mint a new user NFT
```json
{
  "chainType": "Solana",
  "nftContractAddress": "11111111111111111111111111111111",
  "tokenStandard": "ERC721",
  "metadataURI": "https://api.example.com/metadata/123",
  "name": "My User",
  "description": "A unique user representation",
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

#### GET /api/UserNFT/{id}
Get user NFT by ID

#### GET /api/UserNFT/by-token/{chainType}/{contractAddress}/{tokenId}
Get user NFT by blockchain identifiers

#### GET /api/UserNFT/user/{userId}
Get all NFTs for an user

#### POST /api/UserNFT/{id}/transfer
Transfer user NFT to new address
```json
{
  "recipientAddress": "new_owner_address"
}
```

#### DELETE /api/UserNFT/{id}
Burn (delete) user NFT

### Holon Binding Management

#### POST /api/UserNFT/{userNFTId}/holons/{holonId}/bind
Bind holon to user NFT
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

#### GET /api/UserNFT/{userNFTId}/holons
Get all holon bindings for user NFT

#### PUT /api/UserNFT/holons/{bindingId}
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

#### DELETE /api/UserNFT/holons/{bindingId}
Remove holon binding

### Wallet Binding Management

#### POST /api/UserNFT/{userNFTId}/wallets/{walletId}/bind
Bind wallet to user NFT
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

#### GET /api/UserNFT/{userNFTId}/wallets
Get all wallet bindings for user NFT

#### PUT /api/UserNFT/wallets/{bindingId}
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

#### DELETE /api/UserNFT/wallets/{bindingId}
Remove wallet binding

### Composite Views

#### GET /api/UserNFT/{userNFTId}/composite
Get comprehensive view of user NFT with all bindings

#### GET /api/UserNFT/user/{userId}/composite
Get composite views of all NFTs for an user

### Verification

#### POST /api/UserNFT/verify-ownership
Verify user NFT ownership
```json
{
  "chainType": "Solana",
  "nftContractAddress": "11111111111111111111111111111111",
  "tokenId": "123"
}
```

#### POST /api/UserNFT/verify-holon-access
Verify holon access through user NFT
```json
{
  "userNFTId": "guid",
  "holonId": "guid",
  "requiredPermission": "read"
}
```

#### POST /api/UserNFT/verify-wallet-access
Verify wallet access through user NFT
```json
{
  "userNFTId": "guid",
  "walletId": "guid",
  "requiredAccess": "sign"
}
```

## Key Features

### 1. Tight Coupling
- User NFTs serve as the central identity anchor
- Holons and wallets are bound to user NFTs with specific permissions
- All relationships are stored on-chain and off-chain for consistency

### 2. Permission System
- **Role-based access**: Owner, operator, delegate, etc.
- **Fine-grained permissions**: Individual permission flags for specific actions
- **Access levels**: High-level access categories (full, limited, read-only)

### 3. NFT Properties
- **Soulbound NFTs**: Non-transferable user representations
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

### Example 1: Minting an User NFT with Holon Binding

```csharp
// Mint user NFT with holon binding
var mintModel = new UserNFTMintModel
{
    ChainType = "Solana",
    NFTContractAddress = "11111111111111111111111111111111",
    TokenStandard = "ERC721",
    MetadataURI = "https://api.example.com/metadata/123",
    Name = "My User",
    Description = "Main user representation",
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

var result = await _userNFTService.MintUserNFTAsync(userId, mintModel);
```

### Example 2: Composite View

```csharp
// Get comprehensive view of user NFT
var composite = await _userNFTService.GetUserNFTCompositeAsync(nftId);

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
var accessResult = await _userNFTService.VerifyHolonAccessAsync(
    userNFTId, 
    holonId, 
    "execute"
);

if (accessResult.Result)
{
    // Grant access to holon operations
}

// Verify wallet access
var walletAccessResult = await _userNFTService.VerifyWalletAccessAsync(
    userNFTId,
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
dotnet test AZOA.WebAPI.IntegrationTests/Controllers/UserNFTControllerIntegrationTests.cs
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
      "AZOA.WebAPI.Managers.UserNFTService": "Debug"
    }
  }
}
```