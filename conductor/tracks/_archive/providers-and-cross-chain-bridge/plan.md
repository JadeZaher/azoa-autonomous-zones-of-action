# Track: providers-and-cross-chain-bridge — Plan

## Phase 1: Provider Recovery & Wiring
1. [x] Update `ProviderType` enum with Algorand, Solana
2. [x] Add `TryGetModule<T>()` default to `IBlockchainProvider`
3. [x] Create `Core/Blockchain/Base/BaseBlockchainProvider.cs` — shared retry, error handling, config
4. [x] Create `Core/Blockchain/Base/BlockchainConfigurationManager.cs` — config resolution
5. [x] Create `Providers/Blockchain/Algorand/AlgorandProvider.cs` — real SDK integration
6. [x] Create `Providers/Blockchain/Algorand/AlgorandTransactionBuilder.cs` — transaction building
7. [x] Create `Providers/Blockchain/Solana/SolanaProvider.cs` — real SDK integration
8. [x] Create `Providers/Blockchain/Solana/SolanaTransactionBuilder.cs` — transaction building
9. [x] Wire `BlockchainProviderFactory` + providers in `Program.cs`
10. [~] `dotnet build` passes with zero warnings

## Phase 2: Cross-Chain Bridge
11. [x] Create `Interfaces/IBridgeTransaction.cs` — bridge transaction model
12. [x] Create `Interfaces/ICrossChainBridgeService.cs` — bridge interface
13. [x] Create `Models/Responses/BridgeTransactionResult.cs` — bridge result DTO
14. [x] Create `Models/Responses/BridgeRouteInfo.cs` — route info DTO
15. [x] Create `Services/CrossChainBridgeService.cs` — bridge orchestrator
16. [x] Create `Controllers/BridgeController.cs` — bridge REST endpoints
17. [x] Wire `ICrossChainBridgeService` in `Program.cs`
18. [x] `dotnet build` passes with zero warnings

## Phase 3: Testing
19. [x] Unit tests for AlgorandProvider (mocked)
20. [ ] Unit tests for SolanaProvider (mocked)
21. [x] Unit tests for CrossChainBridgeService
22. [x] Unit tests for BlockchainProviderFactory
23. [x] All tests green
24. [x] Update conductor status
