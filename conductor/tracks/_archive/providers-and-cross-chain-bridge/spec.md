# Track: providers-and-cross-chain-bridge — Specification

## Goal
Rebuild and wire real Algorand + Solana blockchain providers, extend `IBlockchainProvider` with cross-chain bridge capabilities, and implement a trusted bridge orchestrator that coordinates asset transfers between chains.

## Motivation
The blockchain provider implementations were deleted. The `IBlockchainProvider` interface exists with mint/burn/transfer/swap/exchange methods but has no implementations. Additionally, the `IBlockchainProvider` lacks cross-chain bridge primitives (lock, mint-wrapped, burn-wrapped, verify-proof).

## Cross-Chain Bridge Architecture

### Is Cross-Chain Bridging Possible?
**Yes**, but as a **trusted/custodial bridge orchestrator** at the API layer. True trustless bridging requires on-chain smart contracts (lock-and-mint, burn-and-mint, or HTLC atomic swaps) and a relayer/validator network. The AZOA WebAPI cannot implement trustless bridging alone.

What AZOA CAN do:
1. **Coordinate** operations across multiple chain providers (lock on Algorand → mint wrapped on Solana)
2. **Track** bridge transactions as special `BlockchainOperation` records
3. **Expose** bridge endpoints for users
4. **Validate** source-chain confirmations before minting on target chain

### Bridge Flow (Trusted Orchestrator)
```
User → AZOA API: "Bridge NFT from Algorand to Solana"
1. AlgorandProvider.LockAsync(tokenId, bridgeVaultAddress)
2. Record BridgeTransaction (status: locked)
3. SolanaProvider.MintAsync(wrappedTokenUri, 1, recipientAddress)
4. Record BridgeTransaction (status: completed)
```

### Bridge Interface
```csharp
public interface ICrossChainBridgeService
{
    Task<AZOAResult<BridgeTransactionResult>> InitiateBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, CancellationToken ct = default);

    Task<AZOAResult<BridgeTransactionResult>> CompleteBridgeAsync(
        string bridgeTransactionId, CancellationToken ct = default);

    Task<AZOAResult<IEnumerable<BridgeTransactionResult>>> GetBridgeHistoryAsync(
        Guid avatarId, CancellationToken ct = default);

    Task<AZOAResult<BridgeRouteInfo>> GetSupportedRoutesAsync(CancellationToken ct = default);
}
```

## Implementation Scope

### Phase 1: Provider Recovery
- Rebuild `AlgorandProvider` with real Algorand2 SDK integration
- Rebuild `SolanaProvider` with real Solana.Rpc/Solana.Wallet SDK integration
- Create `BaseBlockchainProvider` with shared retry, error handling, and HTTP config
- Create `BlockchainConfigurationManager` for network config resolution
- Wire providers in `Program.cs` via `BlockchainProviderFactory`
- Update `ProviderType` enum to include `Algorand`, `Solana`
- Add `TryGetModule<T>()` to `IBlockchainProvider`

### Phase 2: Cross-Chain Bridge
- Add bridge methods to `IBlockchainProvider`:
  - `LockAsync()` — lock asset for bridging
  - `MintWrappedAsync()` — mint wrapped representation
  - `BurnWrappedAsync()` — burn wrapped for reverse bridge
  - `VerifyProofAsync()` — verify cross-chain proof
- Create `ICrossChainBridgeService` interface and `CrossChainBridgeService` implementation
- Create `BridgeTransaction` model
- Create `BridgeController` with REST endpoints
- Wire bridge service in `Program.cs`

### Phase 3: Testing
- Unit tests for AlgorandProvider (mocked SDK)
- Unit tests for SolanaProvider (mocked SDK)
- Unit tests for CrossChainBridgeService
- Integration tests with devnet (optional, requires keys)

## Acceptance Criteria
- [ ] `dotnet build` passes with zero warnings
- [ ] AlgorandProvider connects to devnet and retrieves balances
- [ ] SolanaProvider connects to devnet and retrieves balances
- [ ] `BlockchainProviderFactory` returns correct providers
- [ ] `CrossChainBridgeService` coordinates lock-and-mint flow
- [ ] Bridge endpoints are reachable via Swagger
- [ ] Provider health monitoring works with blockchain providers
- [ ] Unit tests cover provider methods and bridge orchestration

## Dependencies
- `Algorand2` (already in csproj)
- `Solana.Rpc` (already in csproj)
- `Solana.Wallet` (already in csproj)
