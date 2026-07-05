# Track: avatar-nft-service

## Overview

Implement the missing AvatarNFTService manager that connects the AvatarNFTController (19 endpoints) to the storage layer. Fix auth gaps, wire live blockchain balances, and complete InMemory NFT stubs.

## Background

The IAvatarNFTService interface is defined and the AvatarNFTController consumes it, but the implementation class was deleted. The controller also lacks `[Authorize]`, making all 19 endpoints publicly accessible. WalletManager.GetPortfolioAsync returns hardcoded Balance=0 instead of querying blockchain providers.

## Requirements

### FR-1: AvatarNFTService Implementation
- Implement all 19 methods of IAvatarNFTService
- Follow the ProviderContext + Activate pattern used by all other managers
- Delegate to IAZOAStorageProviderNFTExtensions methods on CurrentProvider

### FR-2: Authentication
- Add `[Authorize]` to AvatarNFTController
- Ensure avatarId is extracted from JWT claims consistently

### FR-3: Live Wallet Balances
- Inject IBlockchainProviderFactory into WalletManager
- GetPortfolioAsync queries GetBalanceAsync on the chain provider for the wallet's address
- Graceful fallback to 0 if chain provider is unavailable

### FR-4: InMemoryStorageProvider NFT Stubs
- Complete the 7 stubbed NFT extension methods (Load/Delete operations)
- Enable full test coverage without PostgreSQL

## Acceptance Criteria
- All 19 AvatarNFTController endpoints return proper responses
- AvatarNFT composite views include bound holons and wallets
- Ownership and access verification works end-to-end
- Portfolio shows live native balance from blockchain providers
- InMemory provider passes the same NFT tests as EfStorageProvider
