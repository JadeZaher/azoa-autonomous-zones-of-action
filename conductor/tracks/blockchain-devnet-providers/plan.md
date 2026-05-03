# Blockchain Devnet Providers — Plan

## Tasks

### Phase 1: Algorand Provider Implementation
1. [x] Install Algorand.Net SDK and dependencies
2. [x] Update AlgorandProvider constructor with proper devnet configuration
3. [x] Implement real GetBalanceAsync method using Algorand SDK
4. [x] Implement real ValidateAddressAsync method with format and existence validation
5. [x] Update GetTransactionStatusAsync for real Algorand transaction tracking
6. [x] Implement GetChainInfoAsync with real Algorand network information
7. [x] Add proper error handling and logging for Algorand operations

### Phase 2: Solana Provider Implementation
8. [x] Install Solnet.Rpc and Solnet.Wallet dependencies
9. [x] Update SolanaProvider constructor with proper devnet configuration
10. [x] Implement real GetBalanceAsync method using Solana RPC client
11. [x] Implement real ValidateAddressAsync method with base58 validation
12. [x] Update GetTransactionStatusAsync for real Solana transaction tracking
13. [x] Implement GetChainInfoAsync with real Solana network information
14. [x] Add proper error handling and logging for Solana operations

### Phase 3: Configuration and Setup
15. [ ] Update appsettings.json with Algorand and Solana devnet configuration
16. [ ] Create BlockchainNetworkConfig model for provider configuration
17. [ ] Update ProviderContext to handle devnet/testnet/mainnet switching
18. [ ] Add environment-specific configuration management
19. [ ] Implement configuration validation for provider settings

### Phase 4: Enhanced Provider Features
20. [ ] Implement GetTokensByOwnerAsync for both providers
21. [ ] Update GetTokenMetadataAsync with real token data fetching
22. [ ] Implement TransferAsync with proper transaction simulation
23. [ ] Add network-specific transaction building and signing
24. [ ] Implement proper fee calculation for both chains

### Phase 5: Testing and Validation
25. [ ] Update unit tests for real provider behavior
26. [ ] Create integration tests with devnet connectivity
27. [ ] Add performance tests for provider operations
28. [ ] Test error handling scenarios (network timeouts, invalid addresses)
29. [ ] Validate transaction status tracking accuracy
30. [ ] Test configuration switching between networks

### Phase 6: Documentation and Deployment
31. [ ] Update API documentation with real provider capabilities
32. [ ] Create setup guide for devnet provider configuration
33. [ ] Add troubleshooting guide for common issues
34. [ ] Update README with provider status and capabilities
35. [ ] Create deployment scripts for provider configuration

## Implementation Notes

### Algorand SDK Integration
- Use Algorand.Net for blockchain interactions
- Handle microAlgos to ALGO conversion properly
- Implement transaction simulation for testing
- Add proper error handling for network issues

### Solana RPC Integration
- Use Solnet.Rpc for blockchain interactions
- Handle lamports to SOL conversion properly
- Implement transaction simulation for testing
- Add proper error handling for RPC timeouts

### Configuration Management
- Support multiple network configurations
- Enable runtime network switching
- Validate configuration before provider initialization
- Add logging for debugging purposes

### Error Handling Strategy
- Implement specific exception types for blockchain errors
- Add retry logic for transient failures
- Provide meaningful error messages to users
- Log detailed error information for debugging

### Testing Approach
- Use real devnet endpoints for integration tests
- Mock network failures for error testing
- Test both successful and failed scenarios
- Validate transaction status accuracy

## Dependencies to Install
```xml
<!-- Algorand Provider -->
<PackageReference Include="Algorand.Net" Version="2.0.0" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.0" />

<!-- Solana Provider -->
<PackageReference Include="Solnet.Rpc" Version="0.42.0" />
<PackageReference Include="Solnet.Wallet" Version="0.42.0" />
```

## Configuration Template
```json
{
  "Blockchain": {
    "Algorand": {
      "Devnet": {
        "NodeUrl": "https://testnet-api.algonode.cloud",
        "ApiToken": "",
        "TimeoutMs": 30000,
        "EnableLogging": true
      },
      "Testnet": {
        "NodeUrl": "https://testnet-api.algonode.cloud",
        "ApiToken": "",
        "TimeoutMs": 30000,
        "EnableLogging": true
      }
    },
    "Solana": {
      "Devnet": {
        "NodeUrl": "https://api.devnet.solana.com",
        "TimeoutMs": 30000,
        "EnableLogging": true
      },
      "Testnet": {
        "NodeUrl": "https://api.testnet.solana.com",
        "TimeoutMs": 30000,
        "EnableLogging": true
      }
    }
  }
}
```

## Testing Strategy
- Unit tests for individual provider methods
- Integration tests with real devnet endpoints
- Performance tests for transaction processing
- Error scenario testing for network failures
- Configuration validation testing