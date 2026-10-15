# Track: user-nft-service

## Overview

Implement the missing UserNFTService manager that connects the UserNFTController (19 endpoints) to the storage layer. Fix auth gaps, wire live blockchain balances, and complete InMemory NFT stubs.

## Background

The IUserNFTService interface is defined and the UserNFTController consumes it, but the implementation class was deleted. The controller also lacks `[Authorize]`, making all 19 endpoints publicly accessible. WalletManager.GetPortfolioAsync returns hardcoded Balance=0 instead of querying blockchain providers.

## Requirements

### FR-1: UserNFTService Implementation
- Implement all 19 methods of IUserNFTService
- Follow the ProviderContext + Activate pattern used by all other managers
- Delegate to IAZOAStorageProviderNFTExtensions methods on CurrentProvider

### FR-2: Authentication
- Add `[Authorize]` to UserNFTController
- Ensure userId is extracted from JWT claims consistently

### FR-3: Live Wallet Balances
- Inject IBlockchainProviderFactory into WalletManager
- GetPortfolioAsync queries GetBalanceAsync on the chain provider for the wallet's address
- Graceful fallback to 0 if chain provider is unavailable

### FR-4: InMemoryStorageProvider NFT Stubs
- Complete the 7 stubbed NFT extension methods (Load/Delete operations)
- Enable full test coverage without PostgreSQL

## Acceptance Criteria
- All 19 UserNFTController endpoints return proper responses
- UserNFT composite views include bound holons and wallets
- Ownership and access verification works end-to-end
- Portfolio shows live native balance from blockchain providers
- InMemory provider passes the same NFT tests as EfStorageProvider
